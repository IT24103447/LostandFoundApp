using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests.Integration;

/// <summary>
/// Named "...IntegrationTests" (not "PasswordResetFlowTests") to avoid colliding with
/// the existing mocked unit test class of a similar name in AuthService.Tests.Controllers.
///
/// Covers forgot-password -> reset-password -> login-with-new-password against the
/// real database, proving the OTP hash comparison, password complexity validation,
/// and new-password hashing all actually work together — not just in isolation.
/// </summary>
public class PasswordResetFlowIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PasswordResetFlowIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ForgotPassword_ResetPassword_Login_FullFlow_Succeeds()
    {
        var user = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);
        const string newPassword = "N3wStr0ng!Passw0rd";

        // --- 1. Forgot password ---
        var forgotResponse = await _client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest
        {
            Email = user.Email
        });
        Assert.Equal(HttpStatusCode.OK, forgotResponse.StatusCode);
        var forgotBody = await forgotResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionToken = forgotBody.GetProperty("sessionToken").GetString();
        Assert.False(string.IsNullOrEmpty(sessionToken));

        var code = _factory.FakeEmail.GetLastOtpCodeFor(user.Email);

        // --- 2. Reset password with the real OTP ---
        var resetResponse = await _client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest
        {
            SessionToken = sessionToken!,
            Code = code,
            NewPassword = newPassword
        });
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        // User was already verified, so a successful reset logs them straight in.
        Assert.False(string.IsNullOrEmpty(resetResponse.ExtractCookieValue("auth_token")));

        // --- 3. Old password no longer works ---
        var loginWithOldPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = user.Password
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithOldPassword.StatusCode);

        // --- 4. New password works, against the real stored hash ---
        var loginWithNewPassword = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = newPassword
        });
        Assert.Equal(HttpStatusCode.OK, loginWithNewPassword.StatusCode);
    }

    [Fact]
    public async Task ResetPassword_WithWrongCode_Returns400_AndDoesNotChangePassword()
    {
        var user = await TestUserFactory.RegisterAndVerifyAsync(_client, _factory.FakeEmail);

        var forgotResponse = await _client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest
        {
            Email = user.Email
        });
        var forgotBody = await forgotResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionToken = forgotBody.GetProperty("sessionToken").GetString();

        var resetResponse = await _client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest
        {
            SessionToken = sessionToken!,
            Code = "000000", // deliberately wrong
            NewPassword = "DoesntMatter1!Password"
        });

        Assert.Equal(HttpStatusCode.BadRequest, resetResponse.StatusCode);

        // Original password must still work — proves the wrong-code path really left the DB untouched.
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = user.Password
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }
}
