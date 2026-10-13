using AuthService.Models.Dtos;
using AuthService.Models.Events;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.AdminUserManagement;

public class AdminKickTests
{
    private readonly AuthControllerTestContext _context = new();

    [Fact]
    public async Task Confirmed_admin_kick_sets_suspension_publishes_actor_event_and_blocks_subsequent_login()
    {
        var adminId = Guid.NewGuid();
        var user = AuthControllerTestContext.User();
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _context.Users.Setup(x => x.GetByIdAsync(user.Id, ct)).ReturnsAsync(user);
        var sequence = new MockSequence();
        _context.Users.InSequence(sequence).Setup(x => x.SetKickedAsync(user.Id, true, ct))
            .Callback<Guid, bool, CancellationToken>((_, kicked, _) => user.IsKicked = kicked).Returns(Task.CompletedTask);
        _context.Publisher.InSequence(sequence).Setup(x => x.PublishAsync($"{AuthControllerTestContext.TopicPrefix}.user.kicked",
            It.Is<UserKickedEvent>(e => e.UserId == user.Id && e.KickedBy == adminId && e.Email == user.Email && e.Name == user.Name && e.Phone == user.PhoneNo), ct))
            .Returns(ValueTask.CompletedTask);
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, ct)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify("CurrentPassword123", user.PasswordHash)).Returns(true);

        var kick = Assert.IsType<OkObjectResult>(await _context.Admin(adminId).KickUser(user.Id, new ConfirmRequest { Confirm = true }, ct));

        var body = AuthControllerTestContext.Body(kick);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal($"User {user.Email} has been kicked.", body.GetProperty("message").GetString());
        var loginController = _context.Controller();
        var login = await loginController.Login(new LoginRequest { Email = user.Email, Password = "CurrentPassword123" }, ct);
        var rejected = Assert.IsType<ObjectResult>(login.Result);
        Assert.Equal(403, rejected.StatusCode);
        Assert.Equal("Your account has been suspended.", AuthControllerTestContext.Body(rejected).GetProperty("error").GetString());
        Assert.False(loginController.Response.Headers.ContainsKey("Set-Cookie"));
        _context.Users.Verify(x => x.SetKickedAsync(user.Id, true, ct), Times.Once);
        _context.Publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<UserKickedEvent>(), ct), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("NoConfirmation", 400, "Confirmation is required. Set confirm to true.")]
    [InlineData("Self", 400, "You cannot kick yourself.")]
    [InlineData("MissingUser", 404, "User not found.")]
    [InlineData("AdminTarget", 400, "Cannot kick an admin account.")]
    [InlineData("AlreadyKicked", 400, "User is already kicked.")]
    public async Task Invalid_admin_kick_rejects_without_suspension_or_publication(string scenario, int status, string error)
    {
        var user = AuthControllerTestContext.User();
        var adminId = scenario == "Self" ? user.Id : Guid.NewGuid();
        user.IsAdmin = scenario == "AdminTarget";
        user.IsKicked = scenario == "AlreadyKicked";
        if (scenario is not ("NoConfirmation" or "Self"))
            _context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(scenario == "MissingUser" ? null : user);

        var result = Assert.IsAssignableFrom<ObjectResult>(await _context.Admin(adminId).KickUser(user.Id,
            new ConfirmRequest { Confirm = scenario != "NoConfirmation" }, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        _context.AssertAllCallsVerified();
    }
}
