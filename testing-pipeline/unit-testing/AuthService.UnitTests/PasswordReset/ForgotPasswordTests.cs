using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.PasswordReset;

public class ForgotPasswordTests
{
    private readonly AuthControllerTestContext _context = new();

    [Fact]
    public async Task Registered_email_invalidates_old_reset_codes_and_emails_hashed_five_minute_otp()
    {
        var user = AuthControllerTestContext.User();
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        PasswordResetToken? saved = null;
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, ct)).ReturnsAsync(user);
        var sequence = new MockSequence();
        _context.ResetTokens.InSequence(sequence).Setup(x => x.InvalidateAllForUserAsync(user.Id, ct)).Returns(Task.CompletedTask);
        _context.Otp.InSequence(sequence).Setup(x => x.GenerateCode()).Returns("123456");
        _context.Otp.InSequence(sequence).Setup(x => x.Hash("123456")).Returns("reset-code-hash");
        _context.ResetTokens.InSequence(sequence).Setup(x => x.CreateAsync(It.IsAny<PasswordResetToken>(), ct))
            .Callback<PasswordResetToken, CancellationToken>((token, _) => saved = token).Returns(Task.CompletedTask);
        _context.Email.InSequence(sequence).Setup(x => x.SendAsync(user.Email, "Your Lost & Found password reset code",
            It.Is<string>(s => s.Contains("123456") && s.Contains("5 minutes")),
            It.Is<string?>(s => s != null && s.Contains("123456") && s.Contains("5 minutes")), ct)).Returns(Task.CompletedTask);
        _context.Session.InSequence(sequence).Setup(x => x.Issue(user.Id)).Returns("reset-session");
        var started = DateTime.UtcNow;

        var result = Assert.IsType<OkObjectResult>(await _context.Controller().ForgotPassword(new ForgotPasswordRequest { Email = user.Email }, ct));

        Assert.Equal("reset-session", AuthControllerTestContext.Body(result).GetProperty("sessionToken").GetString());
        Assert.NotNull(saved);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(user.Id, saved.UserId);
        Assert.Equal("reset-code-hash", saved.CodeHash);
        Assert.NotEqual("123456", saved.CodeHash);
        Assert.Equal(0, saved.Attempts);
        Assert.Null(saved.UsedAt);
        Assert.InRange(saved.CreatedAt, started, DateTime.UtcNow);
        Assert.InRange(saved.ExpiresAt, started.AddMinutes(5), DateTime.UtcNow.AddMinutes(5));
        _context.ResetTokens.Verify(x => x.InvalidateAllForUserAsync(user.Id, ct), Times.Once);
        _context.Otp.Verify(x => x.GenerateCode(), Times.Once);
        _context.Otp.Verify(x => x.Hash("123456"), Times.Once);
        _context.ResetTokens.Verify(x => x.CreateAsync(saved, ct), Times.Once);
        _context.Email.Verify(x => x.SendAsync(user.Email, "Your Lost & Found password reset code", It.IsAny<string>(), It.IsAny<string?>(), ct), Times.Once);
        _context.Session.Verify(x => x.Issue(user.Id), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("UnknownEmail", 400, "there is no account associated with this email.")]
    [InlineData("SuspendedAccount", 403, "Your account has been suspended. Please contact support.")]
    public async Task Invalid_reset_request_rejects_without_creating_or_sending_code(string scenario, int status, string error)
    {
        var user = AuthControllerTestContext.User();
        user.IsKicked = scenario == "SuspendedAccount";
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None)).ReturnsAsync(scenario == "UnknownEmail" ? null : user);

        var result = Assert.IsAssignableFrom<ObjectResult>(await _context.Controller().ForgotPassword(new ForgotPasswordRequest { Email = user.Email }, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }
}
