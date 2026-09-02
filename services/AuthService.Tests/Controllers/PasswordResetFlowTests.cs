using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthService.Tests.Controllers;

// Unit tests for AuthController.ForgotPassword / ResetPassword, covering the Password
// Reset story's 6 acceptance criteria. Every dependency is mocked — no real database,
// SMTP, or JWT signing infrastructure needed. PasswordValidator is used as a real
// instance (pure/stateless), matching the convention in AuthControllerRegisterTests.cs
// and VerificationFlowTests.cs.
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - Request OTP              -> covered
//   Scenario 2 - Successful Reset         -> covered
//   Scenario 3 - Incorrect OTP            -> covered
//   Scenario 4 - Too Many Failed Attempts -> covered, but see NOTE on MaxOtpAttempts below
//   Scenario 5 - Expired OTP              -> covered
//   Scenario 6 - Weak New Password        -> covered
public class PasswordResetFlowTests
{
    private readonly Mock<IUsersRepository> _users = new();
    private readonly Mock<IEmailVerificationTokensRepository> _tokens = new();
    private readonly Mock<IPasswordResetTokensRepository> _resetTokens = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ITokenGenerator> _tokenGenerator = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IVerificationSessionService> _sessionService = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IEventPublisher> _publisher = new();

    // NOTE: MaxOtpAttempts is set to 5 here (matching the story's stated "locks after 5
    // wrong attempts"), NOT the 10 currently configured in appsettings.json. This lets us
    // verify the CONTROLLER'S boundary logic (`Attempts + 1 >= MaxOtpAttempts`) is correct
    // in isolation from the separate configuration question. This is the SAME discrepancy
    // already flagged as DEF-05 against the Email Verification story (VerificationFlowTests.cs)
    // — both flows read the identical Auth:MaxOtpAttempts setting, so this isn't a new bug,
    // just DEF-05 resurfacing in a second flow. Not something a unit test should silently
    // paper over by matching the wrong config value.
    private readonly AuthSettings _authSettings = new()
    {
        OtpExpiryMinutes = 10,
        MaxOtpAttempts = 5,
        ResendCooldownSeconds = 60,
        VerificationSessionMinutes = 30
    };

    private AuthController BuildController()
    {
        var controller = new AuthController(
            _users.Object,
            _tokens.Object,
            _resetTokens.Object,
            _passwordHasher.Object,
            new PasswordValidator(), // real instance — pure/stateless logic
            _tokenGenerator.Object,
            _email.Object,
            _sessionService.Object,
            _jwtTokenService.Object,
            _publisher.Object,
            Options.Create(_authSettings),
            Options.Create(new JwtSettings
            {
                Secret = "test-secret",
                Issuer = "test",
                Audience = "test",
                ExpiryMinutes = 60
            }),
            Options.Create(new KafkaSettings()),
            Mock.Of<ILogger<AuthController>>());

        // The controller touches HttpContext.Request.Cookies, Response.Cookies, and
        // HttpContext.RequestServices (for IHostEnvironment) — give it a real, empty
        // DefaultHttpContext with a working (if mostly empty) service provider so those
        // calls don't null-reference in a unit test with no real ASP.NET Core pipeline.
        var services = new ServiceCollection().BuildServiceProvider();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        return controller;
    }

    private static User MakeUser(bool verified = true, bool kicked = false) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        Name = "Test User",
        PhoneNo = "+94771234567",
        PasswordHash = "old-hash",
        IsAdmin = false,
        IsEmailVerified = verified,
        IsKicked = kicked,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static PasswordResetToken MakeResetToken(
        Guid userId, string codeHash, int attempts = 0, DateTime? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CodeHash = codeHash,
        ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(5),
        Attempts = attempts,
        UsedAt = null,
        CreatedAt = DateTime.UtcNow
    };

    private static string? GetErrorMessage(object? value) =>
        value?.GetType().GetProperty("error")?.GetValue(value) as string;

    // ========================= ForgotPassword =========================

    // ---------- Scenario 1: Request OTP ----------

    [Fact]
    public async Task ForgotPassword_ValidRegisteredEmail_CreatesOtp_SendsEmail_ReturnsSessionToken()
    {
        var user = MakeUser();
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokenGenerator.Setup(t => t.GenerateCode()).Returns("123456");
        _tokenGenerator.Setup(t => t.Hash("123456")).Returns("hashed-123456");
        _sessionService.Setup(s => s.Issue(user.Id)).Returns("session-abc");

        var controller = BuildController();
        var result = await controller.ForgotPassword(
            new ForgotPasswordRequest { Email = user.Email }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var sessionTokenProp = ok.Value!.GetType().GetProperty("sessionToken");
        Assert.NotNull(sessionTokenProp);
        Assert.Equal("session-abc", sessionTokenProp!.GetValue(ok.Value));

        // Any pre-existing code for this user must be invalidated before issuing a new
        // one, so an old code can't be used alongside the new one.
        _resetTokens.Verify(r => r.InvalidateAllForUserAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);

        // Per AC: 6-digit OTP with a short (5 minute) expiry.
        _resetTokens.Verify(r => r.CreateAsync(
            It.Is<PasswordResetToken>(t =>
                t.UserId == user.Id &&
                t.CodeHash == "hashed-123456" &&
                t.Attempts == 0 &&
                t.UsedAt == null &&
                t.ExpiresAt > DateTime.UtcNow.AddMinutes(4) &&
                t.ExpiresAt <= DateTime.UtcNow.AddMinutes(5).AddSeconds(5)),
            It.IsAny<CancellationToken>()), Times.Once);

        _email.Verify(e => e.SendAsync(
            user.Email,
            It.IsAny<string>(),
            It.Is<string>(body => body.Contains("123456")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_ReturnsBadRequest_DoesNotCreateTokenOrSendEmail()
    {
        _users.Setup(u => u.GetByEmailAsync("nobody@example.com", It.IsAny<CancellationToken>()))
              .ReturnsAsync((User?)null);

        var controller = BuildController();
        var result = await controller.ForgotPassword(
            new ForgotPasswordRequest { Email = "nobody@example.com" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _resetTokens.Verify(r => r.CreateAsync(It.IsAny<PasswordResetToken>(), It.IsAny<CancellationToken>()), Times.Never);
        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForgotPassword_KickedUser_Returns403_DoesNotSendEmail()
    {
        var user = MakeUser(kicked: true);
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController();
        var result = await controller.ForgotPassword(
            new ForgotPasswordRequest { Email = user.Email }, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========================= ResetPassword =========================

    // ---------- Scenario 2: Successful Reset ----------

    [Fact]
    public async Task ResetPassword_CorrectCodeAndValidPassword_UpdatesPassword_MarksTokenUsed_IssuesLoginCookie()
    {
        var user = MakeUser();
        var token = MakeResetToken(user.Id, "hashed-123456");

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _tokenGenerator.Setup(t => t.Hash("123456")).Returns("hashed-123456");
        _passwordHasher.Setup(h => h.Hash("NewPass123")).Returns("new-hash");
        _jwtTokenService.Setup(j => j.IssueLoginToken(user)).Returns("fake.jwt.token");

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "123456",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        // Per AC: validates the OTP, hashes and stores the new password.
        _users.Verify(u => u.UpdatePasswordHashAsync(user.Id, "new-hash", It.IsAny<CancellationToken>()), Times.Once);
        _resetTokens.Verify(r => r.MarkUsedAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);

        // Per AC: "allows login with the new password" — the endpoint actually goes
        // further and logs the user in immediately (issues the auth cookie), rather than
        // requiring a separate trip through the login form. Worth confirming this is the
        // intended UX (vs. AC's literal wording, which only requires login to be POSSIBLE
        // afterward) when reviewing with product/design.
        Assert.True(controller.ControllerContext.HttpContext.Response.Headers.ContainsKey("Set-Cookie"));
    }

    // ---------- Scenario 3: Incorrect OTP ----------

    [Fact]
    public async Task ResetPassword_IncorrectCode_ReturnsBadRequest_IncrementsAttempts_DoesNotChangePassword()
    {
        var user = MakeUser();
        var token = MakeResetToken(user.Id, "hashed-CORRECT", attempts: 1);

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "999999",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);

        // Per AC: increments a failed-attempt counter and allows retry.
        _resetTokens.Verify(r => r.IncrementAttemptsAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.UpdatePasswordHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _resetTokens.Verify(r => r.MarkUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 4: Too Many Failed Attempts ----------

    [Fact]
    public async Task ResetPassword_AttemptBelowLockoutBoundary_DoesNotLockToken()
    {
        // One below the lockout boundary (test config: MaxOtpAttempts = 5) — should
        // still just increment, not lock. 3 + 1 = 4 < 5.
        var user = MakeUser();
        var token = MakeResetToken(user.Id, "hashed-CORRECT", attempts: 3);

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "999999",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid or expired", GetErrorMessage(badRequest.Value), StringComparison.OrdinalIgnoreCase);
        _resetTokens.Verify(r => r.MarkUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_FifthWrongAttempt_LocksToken_RequiresNewCode()
    {
        // MaxOtpAttempts = 5 (test config). Attempts already = 4 means this wrong guess
        // is the 5th recorded failure — the boundary check is
        // `token.Attempts + 1 >= MaxOtpAttempts`, i.e. 4 + 1 >= 5 → true → lock.
        var user = MakeUser();
        var token = MakeResetToken(user.Id, "hashed-CORRECT", attempts: 4);

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "999999",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("too many", GetErrorMessage(badRequest.Value), StringComparison.OrdinalIgnoreCase);

        // Token gets locked (marked used) so it can no longer be retried — user must
        // request a fresh OTP instead, per the acceptance criteria.
        _resetTokens.Verify(r => r.MarkUsedAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.UpdatePasswordHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 5: Expired OTP ----------

    [Fact]
    public async Task ResetPassword_NoActiveToken_ReturnsExpiryError()
    {
        // Simulates an expired OTP: the repository's GetActiveByUserIdAsync filters out
        // expired tokens server-side (expires_at > UTC_TIMESTAMP(3)), so this looks
        // identical — from the controller's point of view — to "no active token exists
        // at all". Note this means the same message ("Invalid or expired reset code.")
        // covers both "you typed the wrong code" and "your code actually expired" — the
        // controller can't tell them apart from here, hence the deliberately generic
        // wording. Worth a UX gut-check against AC5's "shows an expiry error", since a
        // user who let their code expire and a user who fat-fingered it see identical text.
        var user = MakeUser();

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((PasswordResetToken?)null);

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "123456",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("expired", GetErrorMessage(badRequest.Value), StringComparison.OrdinalIgnoreCase);
        _resetTokens.Verify(r => r.IncrementAttemptsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 6: Weak New Password ----------

    [Fact]
    public async Task ResetPassword_WeakNewPassword_ReturnsValidationProblem_DoesNotChangePassword()
    {
        var user = MakeUser();
        var token = MakeResetToken(user.Id, "hashed-123456");

        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _resetTokens.Setup(r => r.GetActiveByUserIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _tokenGenerator.Setup(t => t.Hash("123456")).Returns("hashed-123456");

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "123456",
            NewPassword = "weak" // fails complexity rules (real PasswordValidator instance)
        }, CancellationToken.None);

        var problem = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, problem.StatusCode);

        // Per AC: rejects it and displays the password requirements — the OTP itself was
        // correct, so this must be rejected on password strength alone, without ever
        // touching the stored hash or burning the (still otherwise-valid) code.
        _users.Verify(u => u.UpdatePasswordHashAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _resetTokens.Verify(r => r.MarkUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Other important guards ----------

    [Fact]
    public async Task ResetPassword_InvalidSessionToken_ReturnsBadRequest()
    {
        _sessionService.Setup(s => s.Validate("bad-token")).Returns((Guid?)null);

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "bad-token",
            Code = "123456",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _resetTokens.Verify(r => r.GetActiveByUserIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPassword_KickedUser_Returns403()
    {
        var user = MakeUser(kicked: true);
        _sessionService.Setup(s => s.Validate("reset-session")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController();
        var result = await controller.ResetPassword(new ResetPasswordRequest
        {
            SessionToken = "reset-session",
            Code = "123456",
            NewPassword = "NewPass123"
        }, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
    }
}
