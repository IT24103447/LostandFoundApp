using System.Text.Json;
using AdminVerifyService.UnitTests.Support;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AdminVerifyService.UnitTests.AdminListingAndUserAccess;

public sealed class AdminListingAndUserAccessTests
{
    [Theory]
    [InlineData(AdminItemType.LOST, "ACTIVE", false)]
    [InlineData(AdminItemType.LOST, "RESOLVED", false)]
    [InlineData(AdminItemType.LOST, "ACTIVE", true)]
    [InlineData(AdminItemType.FOUND, "ACTIVE", false)]
    [InlineData(AdminItemType.FOUND, "RESOLVED", false)]
    [InlineData(AdminItemType.FOUND, "ACTIVE", true)]
    public async Task AdminReadsListingsInEveryState(AdminItemType type, string status, bool deleted)
    {
        var ctx = new AdminItemsContext();
        var item = AdminItemsContext.Record(type, status, deleted);
        ctx.Items.Setup(x => x.GetByIdAsync(type, item.Id, CancellationToken.None)).ReturnsAsync(item);

        var result = await ctx.Controller.GetItem(type.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);
        var dto = Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(item.Status, dto.Status);
        Assert.Equal(item.CreatedAt, dto.CreatedAt);
        Assert.Equal(item.DeletedAt, dto.DeletedAt);
        ctx.Events.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task AdminDeletePublishesOwnerIdentityAndLogsTheActingAdmin(AdminItemType type)
    {
        var ctx = new AdminItemsContext();
        var item = AdminItemsContext.Record(type);
        ctx.Items.Setup(x => x.GetByIdAsync(type, item.Id, CancellationToken.None)).ReturnsAsync(item);
        ctx.Items.Setup(x => x.SoftDeleteActiveAsync(type, item.Id, It.IsAny<DateTime>(), CancellationToken.None)).ReturnsAsync(true);
        ItemDeleteRequestedEvent? sent = null;
        ctx.Events.Setup(x => x.PublishAsync("items.item.delete_requested", It.IsAny<ItemDeleteRequestedEvent>(), CancellationToken.None))
            .Callback<string, ItemDeleteRequestedEvent, CancellationToken>((_, payload, _) => sent = payload)
            .Returns(ValueTask.CompletedTask);

        Assert.IsType<OkObjectResult>(await ctx.Controller.DeleteItem(type.ToString().ToLowerInvariant(), item.Id, CancellationToken.None));

        Assert.NotNull(sent);
        Assert.Equal(item.Id, sent.ItemId);
        Assert.Equal(item.UserId, sent.UserId);
        Assert.Equal(type.ToString(), sent.ItemType);
        ctx.Items.VerifyAll();
        ctx.Events.VerifyAll();
        ctx.Log.Verify(x => x.Log(LogLevel.Information, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(ctx.AdminId.ToString())),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("deleted", 404)]
    [InlineData("resolved", 409)]
    [InlineData("raced", 409)]
    public async Task InvalidDeleteDoesNotPublishAnEvent(string scenario, int expected)
    {
        var ctx = new AdminItemsContext();
        var item = AdminItemsContext.Record(AdminItemType.LOST,
            scenario == "resolved" ? "RESOLVED" : "ACTIVE", scenario == "deleted");
        ctx.Items.Setup(x => x.GetByIdAsync(AdminItemType.LOST, item.Id, CancellationToken.None))
            .ReturnsAsync(scenario == "missing" ? null : item);
        if (scenario == "raced")
            ctx.Items.Setup(x => x.SoftDeleteActiveAsync(AdminItemType.LOST, item.Id, It.IsAny<DateTime>(), CancellationToken.None)).ReturnsAsync(false);

        var result = Assert.IsAssignableFrom<ObjectResult>(await ctx.Controller.DeleteItem("lost", item.Id, CancellationToken.None));

        Assert.Equal(expected, result.StatusCode);
        ctx.Events.VerifyNoOtherCalls();
        if (scenario != "raced")
            ctx.Items.Verify(x => x.SoftDeleteActiveAsync(It.IsAny<AdminItemType>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserLookupReturnsOnlyLiveContactFieldsIncludingKickedUsers(bool kicked)
    {
        var users = new Mock<IUsersRepository>(MockBehavior.Strict);
        var user = new User { Id = Guid.NewGuid(), Name = "Test User", Email = "user@example.test",
            PhoneNo = "+94770000001", IsKicked = kicked, PasswordHash = "private-hash" };
        users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(user);
        var controller = UserController(users);

        var result = await controller.GetUserContact(user.Id, CancellationToken.None);
        var contact = Assert.IsType<AdminUserContactDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(user.Name, contact.Name);
        Assert.Equal(user.Email, contact.Email);
        Assert.Equal(user.PhoneNo, contact.PhoneNo);
        Assert.DoesNotContain("private-hash", JsonSerializer.Serialize(contact));
        Assert.Equal(3, typeof(AdminUserContactDto).GetProperties().Length);
    }

    [Fact]
    public async Task FilteredOutDeletedOrUnknownUserReturns404()
    {
        var users = new Mock<IUsersRepository>(MockBehavior.Strict);
        var id = Guid.NewGuid();
        users.Setup(x => x.GetByIdAsync(id, CancellationToken.None)).ReturnsAsync((User?)null);
        Assert.IsType<NotFoundObjectResult>((await UserController(users).GetUserContact(id, CancellationToken.None)).Result);
    }

    private static AuthService.Controllers.AdminController UserController(Mock<IUsersRepository> users) => new(
        users.Object, Mock.Of<AuthService.Services.IEventPublisher>(),
        Options.Create(new AuthService.Configuration.KafkaSettings()),
        NullLogger<AuthService.Controllers.AdminController>.Instance);
}
