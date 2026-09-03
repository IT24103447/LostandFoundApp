using System.Net;
using System.Net.Http.Json;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests.Integration;

/// <summary>
/// Covers the core account lifecycle end-to-end against the REAL app: real MySQL,
/// real password hashing, real JWT issuance/validation through the real auth
/// middleware, real Kafka-event attempt (captured by FakeEventPublisher).
///
/// This is the layer above the unit tests in AuthControllerRegisterTests /
/// VerificationFlowTests / AuthControllerLoginTests, which mock every dependency.
/// If a SQL query is wrong, a migration is missing a column, or a JWT claim doesn't
/// round-trip through the [Authorize] middleware, THIS is the test that catches it —
/// none of that is visible when the repository is mocked.
/// </summary>
public class RegisterVerifyLoginFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public RegisterVerifyLoginFlowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static long _phoneCounter;

    [Fact]
    public async Task Register_Verify_Logout_Login_Me_AllSucceed_AndPersistToRealDatabase()
    {
        var unique = Guid.NewGuid().ToString("N")[..10];
        var email = $"flow_{unique}@example.com";
        var password = "Str0ng!Passw0rd";
        // E.164 requires '+' then digits only — build the phone from a counter, not the hex guid.
        var phoneDigits = Interlocked.Increment(ref _phoneCounter).ToString().PadLeft(8, '0');
        var phone = $"+947{phoneDigits[^7..]}";

        // --- 1. Register ---
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = email,
            Password = password,
            Name = "Flow Test User",
            PhoneNo = phone
        });

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        var registerBody = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>();
        Assert.NotNull(registerBody);
        Assert.Equal(email, registerBody!.Email);
        Assert.False(registerBody.IsEmailVerified);
        Assert.False(string.IsNullOrEmpty(registerBody.VerificationSessionToken));

        // Proves the row is REALLY in MySQL, not just returned in the response —
        // a second register with the same email must now be rejected as a conflict.
        var otherPhoneDigits = Interlocked.Increment(ref _phoneCounter).ToString().PadLeft(8, '0');
        var duplicateResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = email,
            Password = password,
            Name = "Someone Else",
            PhoneNo = $"+947{otherPhoneDigits[^7..]}"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        // --- 2. Verify email using the OTP the app "sent" (captured by FakeEmailService) ---
        var code = _factory.FakeEmail.GetLastOtpCodeFor(email);
        var verifyResponse = await _client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest
        {
            SessionToken = registerBody.VerificationSessionToken,
            Code = code
        });

        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var authCookieAfterVerify = verifyResponse.ExtractCookieValue("auth_token");
        Assert.False(string.IsNullOrEmpty(authCookieAfterVerify)); // verify-email logs the user straight in

        // Real Kafka publish was attempted with the right topic (captured, not sent to a real broker).
        Assert.True(_factory.FakeEvents.WasPublishedTo("user.verified"));

        // --- 3. /me works with the cookie issued at verify time ---
        var meRequest = CookieTestHelpers.NewJsonRequest(HttpMethod.Get, "/api/auth/me")
            .WithCookie("auth_token", authCookieAfterVerify!);
        var meResponse = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        var profile = await meResponse.Content.ReadFromJsonAsync<UserProfileDto>();
        Assert.Equal(email, profile!.Email);
        Assert.True(profile.IsEmailVerified);

        // --- 4. Logout, then confirm the old cookie no longer works implicitly (client discards it) ---
        var logoutRequest = CookieTestHelpers.NewJsonRequest(HttpMethod.Post, "/api/auth/logout")
            .WithCookie("auth_token", authCookieAfterVerify!);
        var logoutResponse = await _client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        // --- 5. Login with real password verification against the real stored hash ---
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(email, loginBody!.Email);

        var authCookieAfterLogin = loginResponse.ExtractCookieValue("auth_token");
        Assert.False(string.IsNullOrEmpty(authCookieAfterLogin));

        // --- 6. /me works again with the freshly issued login cookie ---
        var meRequest2 = CookieTestHelpers.NewJsonRequest(HttpMethod.Get, "/api/auth/me")
            .WithCookie("auth_token", authCookieAfterLogin!);
        var meResponse2 = await _client.SendAsync(meRequest2);
        Assert.Equal(HttpStatusCode.OK, meResponse2.StatusCode);
    }
}
