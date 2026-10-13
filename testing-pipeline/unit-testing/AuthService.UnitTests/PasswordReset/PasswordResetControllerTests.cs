using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.PasswordReset;

public class PasswordResetControllerTests
{
    private readonly AuthControllerTestContext _context = new();
    private static ResetPasswordRequest Request(string password = "NewStrong123") => new()
    {
        SessionToken = "reset-session", Code = "123456", NewPassword = password
    };

    private PasswordResetToken SetupActiveReset(User user, int attempts = 0, CancellationToken ct = default)
    {
        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, CodeHash = "stored-reset-hash", Attempts = attempts,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        _context.Session.Setup(x => x.Validate("reset-session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, ct)).ReturnsAsync(user);
        _context.ResetTokens.Setup(x => x.GetActiveByUserIdAsync(user.Id, ct)).ReturnsAsync(token);
        return token;
    }

    private void SetupSuccessfulReset(User user, PasswordResetToken token, ResetPasswordRequest request, CancellationToken ct = default)
    {
        _context.Otp.Setup(x => x.Hash(request.Code)).Returns(token.CodeHash);
        var sequence = new MockSequence();
        _context.Hasher.InSequence(sequence).Setup(x => x.Hash(request.NewPassword)).Returns("new-password-hash");
        _context.Users.InSequence(sequence).Setup(x => x.UpdatePasswordHashAsync(user.Id, "new-password-hash", ct)).Returns(Task.CompletedTask);
        _context.ResetTokens.InSequence(sequence).Setup(x => x.MarkUsedAsync(token.Id, ct)).Returns(Task.CompletedTask);
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("reset.jwt");
    }

    private void VerifySuccessfulReset(User user, PasswordResetToken token, ResetPasswordRequest request, CancellationToken ct = default)
    {
        _context.Hasher.Verify(x => x.Hash(request.NewPassword), Times.Once);
        _context.Users.Verify(x => x.UpdatePasswordHashAsync(user.Id, "new-password-hash", ct), Times.Once);
        _context.ResetTokens.Verify(x => x.MarkUsedAsync(token.Id, ct), Times.Once);
    }

    [Theory]
    [InlineData("MinimumLength", false)]
    [InlineData("MaximumLength", false)]
    [InlineData("Normal", true)]
    public async Task Correct_reset_code_hashes_valid_password_consumes_code_and_issues_cookie(string scenario, bool production)
    {
        var request = Request(scenario switch
        {
            "MinimumLength" => "Aa1aaaaa", "MaximumLength" => "Aa1" + new string('a', 125), _ => "NewStrong123"
        });
        var user = AuthControllerTestContext.User();
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        var token = SetupActiveReset(user, ct: ct);
        SetupSuccessfulReset(user, token, request, ct);
        var controller = _context.Controller(production);

        var result = Assert.IsType<OkObjectResult>(await controller.ResetPassword(request, ct));

        var body = AuthControllerTestContext.Body(result);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("reset.jwt", body.GetProperty("token").GetString());
        var cookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("auth_token=reset.jwt", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("expires=", cookie);
        Assert.Contains(production ? "samesite=none" : "samesite=lax", cookie);
        Assert.Equal(production, cookie.Contains("; secure"));
        VerifySuccessfulReset(user, token, request, ct);
        _context.Users.Verify(x => x.GetByIdAsync(user.Id, ct), Times.Exactly(2));
        _context.Jwt.Verify(x => x.IssueLoginToken(user), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task Wrong_reset_code_counts_failures_and_consumes_code_at_ten_attempt_limit(int previousAttempts, bool locked)
    {
        var user = AuthControllerTestContext.User();
        var token = SetupActiveReset(user, previousAttempts);
        _context.Otp.Setup(x => x.Hash("123456")).Returns("wrong-hash");
        _context.ResetTokens.Setup(x => x.IncrementAttemptsAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        if (locked) _context.ResetTokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        var controller = _context.Controller();

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ResetPassword(Request(), CancellationToken.None));

        Assert.Equal(locked ? "Too many failed attempts. Please request a new code." : "Invalid or expired reset code.",
            AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.ResetTokens.Verify(x => x.IncrementAttemptsAsync(token.Id, CancellationToken.None), Times.Once);
        _context.ResetTokens.Verify(x => x.MarkUsedAsync(token.Id, CancellationToken.None), locked ? Times.Once() : Times.Never());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("TooShort", "Password must be at least 8 characters long.")]
    [InlineData("TooLong", "Password must be at most 128 characters long.")]
    [InlineData("NoUppercase", "Password must contain at least one uppercase letter.")]
    [InlineData("NoLowercase", "Password must contain at least one lowercase letter.")]
    [InlineData("NoDigit", "Password must contain at least one digit.")]
    public async Task Weak_reset_password_returns_requirements_without_hashing_saving_or_consuming_code(string scenario, string error)
    {
        var password = scenario switch
        {
            "TooShort" => "Aa1aaaa", "TooLong" => "Aa1" + new string('a', 126),
            "NoUppercase" => "abcdefg1", "NoLowercase" => "ABCDEFG1", "NoDigit" => "Abcdefgh",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var token = SetupActiveReset(AuthControllerTestContext.User());
        _context.Otp.Setup(x => x.Hash("123456")).Returns(token.CodeHash);

        var result = Assert.IsAssignableFrom<ObjectResult>(await _context.Controller().ResetPassword(Request(password), CancellationToken.None));

        Assert.Equal(400, result.StatusCode);
        var problem = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.Equal(new[] { error }, problem.Errors["Password"]);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Invalid_reset_session_rejects_before_repository_access()
    {
        _context.Session.Setup(x => x.Validate("reset-session")).Returns((Guid?)null);

        var result = Assert.IsType<BadRequestObjectResult>(await _context.Controller().ResetPassword(Request(), CancellationToken.None));

        Assert.Equal("Invalid or expired reset session.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task No_active_reset_code_returns_expiry_error_without_changing_password()
    {
        var user = AuthControllerTestContext.User();
        _context.Session.Setup(x => x.Validate("reset-session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Otp.Setup(x => x.Hash("123456")).Returns("otp-hash");
        // Repository filters expired/used tokens. This tests its null-result contract, not SQL.
        _context.ResetTokens.Setup(x => x.GetActiveByUserIdAsync(user.Id, CancellationToken.None)).ReturnsAsync((PasswordResetToken?)null);

        var result = Assert.IsType<BadRequestObjectResult>(await _context.Controller().ResetPassword(Request(), CancellationToken.None));

        Assert.Equal("Invalid or expired reset code.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Suspended_account_cannot_reset_password_or_access_reset_codes()
    {
        var user = AuthControllerTestContext.User();
        user.IsKicked = true;
        _context.Session.Setup(x => x.Validate("reset-session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);

        var result = Assert.IsType<ObjectResult>(await _context.Controller().ResetPassword(Request(), CancellationToken.None));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Your account has been suspended.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Unverified_account_password_is_reset_but_authentication_requires_verification()
    {
        var user = AuthControllerTestContext.User(verified: false);
        var request = Request();
        var token = SetupActiveReset(user);
        _context.Otp.Setup(x => x.Hash(request.Code)).Returns(token.CodeHash);
        _context.Hasher.Setup(x => x.Hash(request.NewPassword)).Returns("new-password-hash");
        _context.Users.Setup(x => x.UpdatePasswordHashAsync(user.Id, "new-password-hash", CancellationToken.None)).Returns(Task.CompletedTask);
        _context.ResetTokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Session.Setup(x => x.Issue(user.Id)).Returns("verification-session");
        var controller = _context.Controller();

        var result = Assert.IsType<ObjectResult>(await controller.ResetPassword(request, CancellationToken.None));

        Assert.Equal(403, result.StatusCode);
        var body = AuthControllerTestContext.Body(result);
        Assert.Equal("Email not verified. Please verify your email before signing in.", body.GetProperty("error").GetString());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal("verification-session", body.GetProperty("verificationSessionToken").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        VerifySuccessfulReset(user, token, request);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Consumed_reset_code_cannot_change_password_again_when_repository_returns_no_active_code()
    {
        var user = AuthControllerTestContext.User();
        var request = Request();
        var token = SetupActiveReset(user);
        SetupSuccessfulReset(user, token, request);
        var controller = _context.Controller();
        Assert.IsType<OkObjectResult>(await controller.ResetPassword(request, CancellationToken.None));
        _context.ResetTokens.Setup(x => x.GetActiveByUserIdAsync(user.Id, CancellationToken.None)).ReturnsAsync((PasswordResetToken?)null);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.ResetPassword(request, CancellationToken.None));

        Assert.Equal("Invalid or expired reset code.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        VerifySuccessfulReset(user, token, request);
        _context.Jwt.Verify(x => x.IssueLoginToken(user), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Subsequent_login_verifies_new_password_against_updated_hash()
    {
        var user = AuthControllerTestContext.User();
        var request = Request();
        var token = SetupActiveReset(user);
        _context.Otp.Setup(x => x.Hash(request.Code)).Returns(token.CodeHash);
        _context.Hasher.Setup(x => x.Hash(request.NewPassword)).Returns("new-password-hash");
        // Repository stub stores the value sent by the real reset controller.
        _context.Users.Setup(x => x.UpdatePasswordHashAsync(user.Id, "new-password-hash", CancellationToken.None))
            .Callback<Guid, string, CancellationToken>((_, hash, _) => user.PasswordHash = hash).Returns(Task.CompletedTask);
        _context.ResetTokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("login.jwt");
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify(request.NewPassword, "new-password-hash")).Returns(true);
        Assert.IsType<OkObjectResult>(await _context.Controller().ResetPassword(request, CancellationToken.None));

        var login = await _context.Controller().Login(new LoginRequest { Email = user.Email, Password = request.NewPassword }, CancellationToken.None);

        var response = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(login.Result).Value);
        Assert.Equal(user.Id, response.Id);
        Assert.Equal("login.jwt", response.Token);
        VerifySuccessfulReset(user, token, request);
        _context.Hasher.Verify(x => x.Verify(request.NewPassword, "new-password-hash"), Times.Once);
        _context.Jwt.Verify(x => x.IssueLoginToken(user), Times.Exactly(2));
        _context.AssertAllCallsVerified();
    }
}
