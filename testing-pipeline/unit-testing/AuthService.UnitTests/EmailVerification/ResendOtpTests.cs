using AuthService.Models.Dtos;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.EmailVerification;

public class ResendOtpTests
{
    private readonly AuthControllerTestContext _context = new();

    [Theory]
    [InlineData("FirstResend", false)]
    [InlineData("CooldownElapsed", false)]
    [InlineData("ChangedEmail", true)]
    public async Task Allowed_resend_invalidates_old_codes_stores_hash_and_emails_new_code(string scenario, bool changedEmail)
    {
        var user = AuthControllerTestContext.User(verified: false);
        var request = new ResendVerificationRequest { SessionToken = "session", Email = changedEmail ? "new@example.test" : user.Email };
        _context.Session.Setup(x => x.Validate(request.SessionToken)).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Users.Setup(x => x.GetLastResentAtAsync(user.Id, CancellationToken.None))
            .ReturnsAsync(scenario == "CooldownElapsed" ? DateTime.UtcNow.AddSeconds(-61) : (DateTime?)null);
        if (changedEmail)
            _context.Users.Setup(x => x.IsEmailRegisteredAsync(request.Email, CancellationToken.None)).ReturnsAsync(false);
        var sequence = new MockSequence();
        _context.Tokens.InSequence(sequence).Setup(x => x.InvalidateAllForUserAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Otp.InSequence(sequence).Setup(x => x.GenerateCode()).Returns("654321");
        _context.Otp.InSequence(sequence).Setup(x => x.Hash("654321")).Returns("new-code-hash");
        var earliestExpiry = DateTime.UtcNow.AddMinutes(10);
        _context.Tokens.InSequence(sequence).Setup(x => x.CreateAsync(user.Id, "new-code-hash",
            changedEmail ? request.Email : null, It.IsAny<DateTime>(), CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Email.InSequence(sequence).Setup(x => x.SendAsync(request.Email, "Your Lost & Found verification code",
            It.Is<string>(s => s.Contains("654321")), It.Is<string?>(s => s != null && s.Contains("654321")), CancellationToken.None))
            .Returns(Task.CompletedTask);
        var earliestSent = DateTime.UtcNow;
        _context.Users.InSequence(sequence).Setup(x => x.SetLastResentAtAsync(user.Id, It.IsAny<DateTime>(), CancellationToken.None)).Returns(Task.CompletedTask);

        var result = Assert.IsType<OkObjectResult>(await _context.Controller().ResendVerification(request, CancellationToken.None));

        Assert.True(AuthControllerTestContext.Body(result).GetProperty("sent").GetBoolean());
        _context.Tokens.Verify(x => x.InvalidateAllForUserAsync(user.Id, CancellationToken.None), Times.Once);
        _context.Otp.Verify(x => x.GenerateCode(), Times.Once);
        _context.Otp.Verify(x => x.Hash("654321"), Times.Once);
        _context.Tokens.Verify(x => x.CreateAsync(user.Id, "new-code-hash", changedEmail ? request.Email : null,
            It.Is<DateTime>(t => t >= earliestExpiry && t <= DateTime.UtcNow.AddMinutes(10)), CancellationToken.None), Times.Once);
        _context.Users.Verify(x => x.SetLastResentAtAsync(user.Id,
            It.Is<DateTime>(t => t >= earliestSent && t <= DateTime.UtcNow), CancellationToken.None), Times.Once);
        _context.Email.Verify(x => x.SendAsync(request.Email, "Your Lost & Found verification code",
            It.IsAny<string>(), It.IsAny<string?>(), CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task Resend_inside_cooldown_returns_retry_after_without_side_effects(int elapsedSeconds)
    {
        var user = AuthControllerTestContext.User(verified: false);
        _context.Session.Setup(x => x.Validate("session")).Returns(user.Id);
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Users.Setup(x => x.GetLastResentAtAsync(user.Id, CancellationToken.None))
            .ReturnsAsync(DateTime.UtcNow.AddSeconds(-elapsedSeconds));
        var controller = _context.Controller();

        var result = Assert.IsType<ObjectResult>(await controller.ResendVerification(
            new ResendVerificationRequest { SessionToken = "session", Email = user.Email }, CancellationToken.None));

        Assert.Equal(429, result.StatusCode);
        Assert.Contains("before requesting another code", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        Assert.True(int.TryParse(controller.Response.Headers["Retry-After"], out var remaining));
        Assert.InRange(remaining, 1, 60 - elapsedSeconds);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("InvalidSession", 400, "Invalid or expired verification session.")]
    [InlineData("MissingUser", 400, "Invalid or expired verification session.")]
    [InlineData("SuspendedUser", 403, "Your account has been suspended.")]
    [InlineData("DuplicateEmail", 400, "This email is already registered by another account.")]
    public async Task Invalid_resend_rejects_without_creating_or_sending_code(string scenario, int status, string error)
    {
        var user = AuthControllerTestContext.User(verified: false);
        user.IsKicked = scenario == "SuspendedUser";
        _context.Session.Setup(x => x.Validate("session")).Returns(scenario == "InvalidSession" ? (Guid?)null : user.Id);
        if (scenario != "InvalidSession")
            _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(scenario == "MissingUser" ? null : user);
        if (scenario == "DuplicateEmail")
        {
            _context.Users.Setup(x => x.GetLastResentAtAsync(user.Id, CancellationToken.None)).ReturnsAsync((DateTime?)null);
            _context.Users.Setup(x => x.IsEmailRegisteredAsync("other@example.test", CancellationToken.None)).ReturnsAsync(true);
        }

        var result = Assert.IsAssignableFrom<ObjectResult>(await _context.Controller().ResendVerification(
            new ResendVerificationRequest { SessionToken = "session", Email = "other@example.test" }, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.StartsWith(error, AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }
}
