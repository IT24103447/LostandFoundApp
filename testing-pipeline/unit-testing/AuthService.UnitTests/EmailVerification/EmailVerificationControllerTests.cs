using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Models.Events;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.EmailVerification;

public class EmailVerificationControllerTests
{
    private readonly AuthControllerTestContext _context = new();
    private static VerifyEmailRequest Request() => new() { SessionToken = "session", Code = "123456" };

    private EmailVerificationToken SetupActiveOtp(User user, int attempts = 0, string? pendingEmail = null)
    {
        var token = new EmailVerificationToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, CodeHash = "stored-otp-hash",
            Attempts = attempts, ExpiresAt = DateTime.UtcNow.AddMinutes(10), PendingEmail = pendingEmail
        };
        _context.Session.Setup(x => x.Validate("session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Tokens.Setup(x => x.GetActiveByUserAsync(user.Id, CancellationToken.None)).ReturnsAsync(token);
        return token;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Correct_otp_marks_account_verified_consumes_code_and_issues_auth_cookie(bool production, bool changedEmail)
    {
        var user = AuthControllerTestContext.User(verified: false);
        var token = SetupActiveOtp(user, pendingEmail: changedEmail ? "new@example.test" : null);
        _context.Otp.Setup(x => x.Hash("123456")).Returns(token.CodeHash);
        if (changedEmail)
        {
            _context.Users.Setup(x => x.IsEmailRegisteredAsync(token.PendingEmail!, CancellationToken.None)).ReturnsAsync(false);
            _context.Users.Setup(x => x.UpdateEmailAsync(user.Id, token.PendingEmail!, CancellationToken.None)).Returns(Task.CompletedTask);
        }
        _context.Users.Setup(x => x.MarkEmailVerifiedAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Tokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Publisher.Setup(x => x.PublishAsync($"{AuthControllerTestContext.TopicPrefix}.user.verified",
            It.Is<UserVerifiedEvent>(e => e.UserId == user.Id && e.Email == user.Email && e.Name == user.Name && e.Phone == user.PhoneNo),
            CancellationToken.None)).Returns(ValueTask.CompletedTask);
        // Current code passes the loaded user to JWT issuance without reloading it.
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("issued.jwt");
        var controller = _context.Controller(production);

        var result = Assert.IsType<OkObjectResult>(await controller.VerifyEmail(Request(), CancellationToken.None));

        var body = AuthControllerTestContext.Body(result);
        Assert.True(body.GetProperty("verified").GetBoolean());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal("issued.jwt", body.GetProperty("token").GetString());
        var cookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("auth_token=issued.jwt", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains(production ? "samesite=none" : "samesite=lax", cookie);
        Assert.Equal(production, cookie.Contains("; secure"));
        _context.Users.Verify(x => x.MarkEmailVerifiedAsync(user.Id, CancellationToken.None), Times.Once);
        _context.Tokens.Verify(x => x.MarkUsedAsync(token.Id, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Consumed_otp_cannot_verify_again_when_repository_returns_no_active_code()
    {
        var user = AuthControllerTestContext.User(verified: false);
        var token = SetupActiveOtp(user);
        _context.Otp.Setup(x => x.Hash("123456")).Returns(token.CodeHash);
        _context.Users.Setup(x => x.MarkEmailVerifiedAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Tokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserVerifiedEvent>(), CancellationToken.None)).Returns(ValueTask.CompletedTask);
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("issued.jwt");
        var controller = _context.Controller();
        Assert.IsType<OkObjectResult>(await controller.VerifyEmail(Request(), CancellationToken.None));

        // Stub the repository's post-consumption contract; this does not test its SQL.
        _context.Tokens.Setup(x => x.GetActiveByUserAsync(user.Id, CancellationToken.None)).ReturnsAsync((EmailVerificationToken?)null);
        var result = Assert.IsType<BadRequestObjectResult>(await controller.VerifyEmail(Request(), CancellationToken.None));

        Assert.Equal("Invalid or expired verification code.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.Users.Verify(x => x.MarkEmailVerifiedAsync(user.Id, CancellationToken.None), Times.Once);
        _context.Tokens.Verify(x => x.MarkUsedAsync(token.Id, CancellationToken.None), Times.Once);
        _context.Publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserVerifiedEvent>(), CancellationToken.None), Times.Once);
        _context.Jwt.Verify(x => x.IssueLoginToken(user), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public async Task Incorrect_otp_increments_attempts_and_locks_at_configured_limit(int previousAttempts, bool locked)
    {
        var user = AuthControllerTestContext.User(verified: false);
        var token = SetupActiveOtp(user, previousAttempts);
        _context.Otp.Setup(x => x.Hash("123456")).Returns("wrong-hash");
        _context.Tokens.Setup(x => x.IncrementAttemptsAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        if (locked)
            _context.Tokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        var controller = _context.Controller();

        var result = Assert.IsType<BadRequestObjectResult>(await controller.VerifyEmail(Request(), CancellationToken.None));

        Assert.Equal(locked ? "Too many failed attempts. Please request a new code." : "Invalid or expired verification code.",
            AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.Tokens.Verify(x => x.IncrementAttemptsAsync(token.Id, CancellationToken.None), Times.Once);
        _context.Tokens.Verify(x => x.MarkUsedAsync(token.Id, CancellationToken.None), locked ? Times.Once() : Times.Never());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task No_active_otp_returns_invalid_or_expired_error_without_verification()
    {
        // Expiry/used filtering belongs to the repository. This tests its null-result contract.
        var user = AuthControllerTestContext.User(verified: false);
        _context.Session.Setup(x => x.Validate("session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Otp.Setup(x => x.Hash("123456")).Returns("otp-hash");
        _context.Tokens.Setup(x => x.GetActiveByUserAsync(user.Id, CancellationToken.None)).ReturnsAsync((EmailVerificationToken?)null);

        var result = Assert.IsType<BadRequestObjectResult>(await _context.Controller().VerifyEmail(Request(), CancellationToken.None));

        Assert.Equal("Invalid or expired verification code.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Invalid_verification_session_rejects_before_repository_access()
    {
        _context.Session.Setup(x => x.Validate("session")).Returns((Guid?)null);

        var result = Assert.IsType<BadRequestObjectResult>(await _context.Controller().VerifyEmail(Request(), CancellationToken.None));

        Assert.Equal("Invalid or expired verification session.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(true, false, 400, "Email is already verified.")]
    [InlineData(false, true, 403, "Your account has been suspended.")]
    public async Task Verified_or_suspended_account_rejects_before_otp_access(bool verified, bool suspended, int status, string error)
    {
        var user = AuthControllerTestContext.User(verified);
        user.IsKicked = suspended;
        _context.Session.Setup(x => x.Validate("session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);

        var result = Assert.IsAssignableFrom<ObjectResult>(await _context.Controller().VerifyEmail(Request(), CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Pending_email_claimed_by_another_account_consumes_code_without_verifying()
    {
        var user = AuthControllerTestContext.User(verified: false);
        var token = SetupActiveOtp(user, pendingEmail: "claimed@example.test");
        _context.Otp.Setup(x => x.Hash("123456")).Returns(token.CodeHash);
        _context.Users.Setup(x => x.IsEmailRegisteredAsync(token.PendingEmail!, CancellationToken.None)).ReturnsAsync(true);
        _context.Tokens.Setup(x => x.MarkUsedAsync(token.Id, CancellationToken.None)).Returns(Task.CompletedTask);

        var result = Assert.IsType<BadRequestObjectResult>(await _context.Controller().VerifyEmail(Request(), CancellationToken.None));

        Assert.Contains("claimed by another account", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.Tokens.Verify(x => x.MarkUsedAsync(token.Id, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }
}
