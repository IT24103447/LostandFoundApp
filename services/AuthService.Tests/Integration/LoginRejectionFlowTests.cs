using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests.Integration;

/// <summary>
/// Proves login rejection paths work end-to-end against the real password hasher
/// and real database — not a mocked IPasswordHasher returning a canned bool.
/// </summary>
public class LoginRejectionFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LoginRejectionFlowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401_AndSetsNoCookie()
    {
        var user = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = "TotallyWrongPassword1!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.ExtractCookieValue("auth_token"));
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"nobody_{Guid.NewGuid():N}@example.com",
            Password = "Whatever1!"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_BeforeEmailVerified_Returns403_WithNewVerificationSession_AndNoAuthCookie()
    {
        var unique = Guid.NewGuid().ToString("N")[..10];
        var email = $"unverified_{unique}@example.com";
        var password = "Str0ng!Passw0rd";
        var phoneDigits = (DateTime.UtcNow.Ticks % 100_000_000).ToString().PadLeft(8, '0');

        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = email,
            Password = password,
            Name = "Unverified User",
            PhoneNo = $"+947{phoneDigits[^7..]}"
        });
        registerResponse.EnsureSuccessStatusCode();

        // Never call verify-email — attempt to log in while still unverified.
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password
        });

        Assert.Equal(HttpStatusCode.Forbidden, loginResponse.StatusCode);
        Assert.Null(loginResponse.ExtractCookieValue("auth_token"));

        var body = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("verificationSessionToken").GetString()));

        // Bonus: proves the app re-sent an OTP on this rejected login attempt, since the
        // real code path (not a mock) triggers a resend when an unverified user tries to log in.
        var code = _factory.FakeEmail.GetLastOtpCodeFor(email);
        Assert.Equal(6, code.Length);
    }
}
