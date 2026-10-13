using System.Security.Claims;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Models.Events;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Moq;

namespace AuthService.UnitTests.AccountDeletion;

public class AccountDeletionControllerTests
{
    private readonly AuthControllerTestContext _context = new();
    private static DeleteAccountRequest Request() => new() { Password = "CurrentPassword123" };

    [Theory]
    [InlineData("sub")]
    [InlineData(ClaimTypes.NameIdentifier)]
    public async Task Confirmed_self_deletion_removes_tokens_soft_deletes_publishes_event_and_expires_cookie(string claimType)
    {
        var user = AuthControllerTestContext.User();
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, ct)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify(Request().Password, user.PasswordHash)).Returns(true);
        var sequence = new MockSequence();
        _context.ResetTokens.InSequence(sequence).Setup(x => x.DeleteForUserAsync(user.Id, ct)).Returns(Task.CompletedTask);
        _context.Tokens.InSequence(sequence).Setup(x => x.DeleteForUserAsync(user.Id, ct)).Returns(Task.CompletedTask);
        _context.Users.InSequence(sequence).Setup(x => x.SoftDeleteAsync(user.Id, ct)).Returns(Task.CompletedTask);
        _context.Publisher.InSequence(sequence).Setup(x => x.PublishAsync($"{AuthControllerTestContext.TopicPrefix}.user.deleted",
            It.Is<UserDeletedEvent>(e => e.UserId == user.Id && e.Email == user.Email && e.Name == user.Name && e.Phone == user.PhoneNo), ct))
            .Returns(ValueTask.CompletedTask);
        var controller = _context.Controller(subject: user.Id.ToString(), claimType: claimType);

        var result = Assert.IsType<OkObjectResult>(await controller.DeleteMe(Request(), ct));

        Assert.True(AuthControllerTestContext.Body(result).GetProperty("success").GetBoolean());
        var cookie = SetCookieHeaderValue.Parse(controller.Response.Headers.SetCookie.ToString());
        Assert.Equal("auth_token", cookie.Name.ToString());
        Assert.Equal("", cookie.Value.ToString());
        Assert.NotNull(cookie.Expires);
        Assert.True(cookie.Expires.Value < DateTimeOffset.UtcNow);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal("/", cookie.Path.ToString());
        _context.Hasher.Verify(x => x.Verify(Request().Password, user.PasswordHash), Times.Once);
        _context.ResetTokens.Verify(x => x.DeleteForUserAsync(user.Id, ct), Times.Once);
        _context.Tokens.Verify(x => x.DeleteForUserAsync(user.Id, ct), Times.Once);
        _context.Users.Verify(x => x.SoftDeleteAsync(user.Id, ct), Times.Once);
        _context.Publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserDeletedEvent>(), ct), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-id")]
    public async Task Missing_or_invalid_deletion_identity_rejects_before_repository_access(string? subject)
    {
        var result = Assert.IsType<UnauthorizedObjectResult>(await _context.Controller(subject: subject).DeleteMe(Request(), CancellationToken.None));

        Assert.Equal("Invalid session.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("MissingAccount", 401, "User not found.")]
    [InlineData("SuspendedAccount", 403, "Your account has been suspended.")]
    [InlineData("UnverifiedAccount", 401, "Email not verified.")]
    [InlineData("AdminAccount", 403, "Admin accounts cannot be deleted through this interface.")]
    public async Task Ineligible_self_deletion_rejects_before_password_check_or_side_effects(string scenario, int status, string error)
    {
        var user = AuthControllerTestContext.User(verified: scenario != "UnverifiedAccount");
        user.IsKicked = scenario == "SuspendedAccount";
        user.IsAdmin = scenario == "AdminAccount";
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(scenario == "MissingAccount" ? null : user);
        var controller = _context.Controller(subject: user.Id.ToString());

        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.DeleteMe(Request(), CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Incorrect_confirmation_password_rejects_without_deleting_or_clearing_session()
    {
        var user = AuthControllerTestContext.User();
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify(Request().Password, user.PasswordHash)).Returns(false);
        var controller = _context.Controller(subject: user.Id.ToString());

        var result = Assert.IsType<BadRequestObjectResult>(await controller.DeleteMe(Request(), CancellationToken.None));

        Assert.Equal("Incorrect password.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Post_deletion_login_rejects_repository_filtered_account_without_new_jwt()
    {
        var user = AuthControllerTestContext.User();
        var deleted = false;
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify(Request().Password, user.PasswordHash)).Returns(true);
        _context.ResetTokens.Setup(x => x.DeleteForUserAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Tokens.Setup(x => x.DeleteForUserAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Users.Setup(x => x.SoftDeleteAsync(user.Id, CancellationToken.None))
            .Callback(() => deleted = true).Returns(Task.CompletedTask);
        _context.Publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserDeletedEvent>(), CancellationToken.None)).Returns(ValueTask.CompletedTask);
        // Model the repository's deleted-row filtering after the deletion write.
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None)).ReturnsAsync(() => deleted ? null : user);
        Assert.IsType<OkObjectResult>(await _context.Controller(subject: user.Id.ToString()).DeleteMe(Request(), CancellationToken.None));
        var loginController = _context.Controller();

        var login = await loginController.Login(new LoginRequest { Email = user.Email, Password = Request().Password }, CancellationToken.None);

        var result = Assert.IsType<UnauthorizedObjectResult>(login.Result);
        Assert.Equal("Invalid email or password.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        Assert.False(loginController.Response.Headers.ContainsKey("Set-Cookie"));
        _context.Hasher.Verify(x => x.Verify(Request().Password, user.PasswordHash), Times.Once);
        _context.Users.Verify(x => x.SoftDeleteAsync(user.Id, CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }
}
