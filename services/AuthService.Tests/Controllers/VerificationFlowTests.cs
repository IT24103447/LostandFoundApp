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

// Unit tests for AuthController.VerifyEmail / ResendVerification / VerificationStatus,
// covering the Email Verification story's 6 acceptance criteria scenarios.
// Every dependency is mocked — no real database, SMTP, or JWT signing infrastructure needed.
public class VerificationFlowTests
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

    // NOTE: MaxOtpAttempts is set to 5 here (matching the story's stated "5 wrong attempts
    // then lock on the 6th"), NOT the 10 currently configured in appsettings.json. This lets
    // us verify the CONTROLLER'S boundary logic is correct in isolation from the separate
    // configuration question (see DEF-05: appsettings.json currently has MaxOtpAttempts=10,
    // not 5 — flagged separately, not something a unit test should silently paper over).
    private AuthSettings _authSettings = new()
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
            new PasswordValidator(),
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

    private static User MakeUser(bool verified = false, bool kicked = false) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        Name = "Test User",
        PhoneNo = "+94771234567",
        PasswordHash = "hash",
        IsAdmin = false,
        IsEmailVerified = verified,
        IsKicked = kicked,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static EmailVerificationToken MakeToken(Guid userId, string codeHash, int attempts = 0, string? pendingEmail = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CodeHash = codeHash,
        PendingEmail = pendingEmail,
        ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        Attempts = attempts,
        UsedAt = null,
        CreatedAt = DateTime.UtcNow
    };

    // ========================= VerifyEmail =========================

    // ---------- Scenario 1: Successful Verification ----------

    [Fact]
    public async Task VerifyEmail_CorrectCode_MarksVerified_IssuesJwtCookie_ReturnsOk()
    {
        var user = MakeUser();
        var token = MakeToken(user.Id, "hashed-123456");

        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokenGenerator.Setup(t => t.Hash("123456")).Returns("hashed-123456");
        _tokens.Setup(t => t.GetActiveByUserAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        _jwtTokenService.Setup(j => j.IssueLoginToken(It.IsAny<User>())).Returns("fake.jwt.token");

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "123456" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        _users.Verify(u => u.MarkEmailVerifiedAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
        _tokens.Verify(t => t.MarkUsedAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);

        // A JWT cookie should have been set on the response (per AC: session established
        // after verification so the next request is authenticated).
        Assert.True(controller.ControllerContext.HttpContext.Response.Headers.ContainsKey("Set-Cookie"));
    }

    // ---------- Scenario 2: Incorrect OTP ----------

    [Fact]
    public async Task VerifyEmail_IncorrectCode_ReturnsError_IncrementsAttempts_DoesNotVerify()
    {
        var user = MakeUser();
        var token = MakeToken(user.Id, "hashed-CORRECT", attempts: 0);

        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");
        _tokens.Setup(t => t.GetActiveByUserAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "999999" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);

        _tokens.Verify(t => t.IncrementAttemptsAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.MarkEmailVerifiedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(t => t.MarkUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 3: Too Many Failed Attempts ----------

    [Fact]
    public async Task VerifyEmail_SixthWrongAttempt_LocksToken_PromptsNewOtp()
    {
        var user = MakeUser();
        // MaxOtpAttempts = 5 (per test config above). Attempts already = 4 means this
        // wrong guess is the 5th recorded failure — the boundary check is
        // `token.Attempts + 1 >= MaxOtpAttempts`, i.e. 4 + 1 >= 5 → true → lock.
        var token = MakeToken(user.Id, "hashed-CORRECT", attempts: 4);

        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");
        _tokens.Setup(t => t.GetActiveByUserAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "999999" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var body = badRequest.Value!.ToString();
        Assert.Contains("Too many failed attempts", body);

        // Token gets locked (marked used) so it can no longer be retried — user must
        // request a fresh OTP instead, per the acceptance criteria.
        _tokens.Verify(t => t.MarkUsedAsync(token.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyEmail_FourthWrongAttempt_DoesNotLockYet()
    {
        // One below the lockout boundary — should still just increment, not lock.
        var user = MakeUser();
        var token = MakeToken(user.Id, "hashed-CORRECT", attempts: 3);

        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokenGenerator.Setup(t => t.Hash("999999")).Returns("hashed-WRONG");
        _tokens.Setup(t => t.GetActiveByUserAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(token);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "999999" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _tokens.Verify(t => t.MarkUsedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 4: Expired OTP ----------

    [Fact]
    public async Task VerifyEmail_NoActiveToken_ReturnsExpiryError()
    {
        // Simulates an expired OTP: the repository's GetActiveByUserAsync is expected to
        // filter out expired tokens server-side, so this looks identical (from the
        // controller's point of view) to "no active token exists".
        var user = MakeUser();

        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _tokens.Setup(t => t.GetActiveByUserAsync(user.Id, It.IsAny<CancellationToken>()))
               .ReturnsAsync((EmailVerificationToken?)null);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "123456" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid or expired", badRequest.Value!.ToString());
    }

    // ---------- Other important guards ----------

    [Fact]
    public async Task VerifyEmail_InvalidSession_ReturnsBadRequest()
    {
        _sessionService.Setup(s => s.Validate("bad-token")).Returns((Guid?)null);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "bad-token", Code = "123456" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _tokens.Verify(t => t.GetActiveByUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyEmail_AlreadyVerified_ReturnsBadRequest()
    {
        var user = MakeUser(verified: true);
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "123456" },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("already verified", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task VerifyEmail_KickedUser_Returns403()
    {
        var user = MakeUser(kicked: true);
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController();
        var result = await controller.VerifyEmail(
            new VerifyEmailRequest { SessionToken = "session-token", Code = "123456" },
            CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    // ========================= ResendVerification =========================

    // ---------- Scenario 5: Resend OTP ----------

    [Fact]
    public async Task ResendVerification_NoPriorResend_InvalidatesOldToken_SendsNewOne()
    {
        var user = MakeUser();
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.GetLastResentAtAsync(user.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync((DateTime?)null);
        _tokenGenerator.Setup(t => t.GenerateCode()).Returns("654321");
        _tokenGenerator.Setup(t => t.Hash("654321")).Returns("hashed-654321");

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session-token", Email = user.Email },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);

        _tokens.Verify(t => t.InvalidateAllForUserAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
        _tokens.Verify(t => t.CreateAsync(user.Id, "hashed-654321", null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _email.Verify(e => e.SendAsync(user.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.SetLastResentAtAsync(user.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResendVerification_WithinCooldown_Returns429_WithRetryAfterHeader()
    {
        var user = MakeUser();
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        // Resent 10 seconds ago; cooldown is 60s, so 50s remain.
        _users.Setup(u => u.GetLastResentAtAsync(user.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(DateTime.UtcNow.AddSeconds(-10));

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session-token", Email = user.Email },
            CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, statusResult.StatusCode);
        Assert.True(controller.ControllerContext.HttpContext.Response.Headers.ContainsKey("Retry-After"));

        _tokens.Verify(t => t.InvalidateAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _email.Verify(e => e.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResendVerification_CooldownJustExpired_AllowsResend()
    {
        var user = MakeUser();
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        // Resent 61 seconds ago; cooldown is 60s — should now be allowed.
        _users.Setup(u => u.GetLastResentAtAsync(user.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(DateTime.UtcNow.AddSeconds(-61));
        _tokenGenerator.Setup(t => t.GenerateCode()).Returns("111111");
        _tokenGenerator.Setup(t => t.Hash("111111")).Returns("hashed-111111");

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session-token", Email = user.Email },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ResendVerification_InvalidSession_ReturnsBadRequest()
    {
        _sessionService.Setup(s => s.Validate("bad-token")).Returns((Guid?)null);

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "bad-token", Email = "x@example.com" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ResendVerification_KickedUser_Returns403()
    {
        var user = MakeUser(kicked: true);
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session-token", Email = user.Email },
            CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
    }

    [Fact]
    public async Task ResendVerification_ChangedEmailAlreadyRegistered_ReturnsBadRequest()
    {
        var user = MakeUser();
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.GetLastResentAtAsync(user.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync((DateTime?)null);
        _users.Setup(u => u.IsEmailRegisteredAsync("taken@example.com", It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var controller = BuildController();
        var result = await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session-token", Email = "taken@example.com" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _tokens.Verify(t => t.InvalidateAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========================= VerificationStatus =========================

    // ---------- Scenario 6 support: polling status before/after verification ----------

    [Fact]
    public async Task VerificationStatus_ValidSession_ReturnsCurrentStatus()
    {
        var user = MakeUser(verified: true);
        _sessionService.Setup(s => s.Validate("session-token")).Returns(user.Id);
        _users.Setup(u => u.GetVerificationStatusAsync(user.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new UserVerificationStatus(true));

        var controller = BuildController();
        var result = await controller.VerificationStatus("session-token", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Anonymous object — assert via reflection on the serialized shape.
        var isVerifiedProp = ok.Value!.GetType().GetProperty("isEmailVerified");
        Assert.NotNull(isVerifiedProp);
        Assert.True((bool)isVerifiedProp!.GetValue(ok.Value)!);
    }

    [Fact]
    public async Task VerificationStatus_InvalidSession_ReturnsBadRequest()
    {
        _sessionService.Setup(s => s.Validate("bad-token")).Returns((Guid?)null);

        var controller = BuildController();
        var result = await controller.VerificationStatus("bad-token", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
