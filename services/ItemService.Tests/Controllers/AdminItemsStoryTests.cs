using System.Reflection;
using System.Text.Json;
using ItemService.Authorization;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ItemService.Tests.Controllers;

/// <summary>Story LF-79 contract tests for the admin-only listing read and delete endpoints.</summary>
public sealed class AdminItemsStoryTests
{
    // Story LF-79: admin visibility of any listing, guarded deletion, and the published delete-request contract.
    private const string ActiveStatus = "ACTIVE";
    private static readonly Guid AdminId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Verifies an admin sees every reported field of a live listing, for both kinds.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Get_ExistingListing_Returns200WithEveryReportedField(AdminItemType kind)
    {
        var item = Item(kind);
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(kind, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object).GetItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        var dto = Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal(kind.ToString(), dto.Type);
        Assert.Equal(OwnerId, dto.UserId);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(ActiveStatus, dto.Status);
        Assert.Equal(item.CreatedAt, dto.CreatedAt);
        Assert.Null(dto.DeletedAt);
    }

    // Verifies the admin payload shape is fixed, so a future field (such as hidden_information) cannot leak in.
    [Fact]
    public async Task Get_ExistingListing_ReturnsExactlyTheAgreedPayloadShapeAndNoHiddenInformation()
    {
        var item = Item(AdminItemType.LOST);
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(AdminItemType.LOST, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object).GetItem("lost", item.Id, CancellationToken.None);

        Assert.Equal(
            new[] { "createdAt", "deletedAt", "description", "id", "status", "title", "type", "userId" },
            PayloadKeys(Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value)));
    }

    // Verifies the route segment is matched case-insensitively, because ParseType ignores case.
    [Theory]
    [InlineData("LOST", AdminItemType.LOST)]
    [InlineData("Lost", AdminItemType.LOST)]
    [InlineData("FOUND", AdminItemType.FOUND)]
    [InlineData("Found", AdminItemType.FOUND)]
    public async Task Get_TypeSegment_IsCaseInsensitive(string segment, AdminItemType kind)
    {
        var item = Item(kind);
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(kind, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object).GetItem(segment, item.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // Verifies an unknown listing reports the shared not-found error rather than leaking a bare 404.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Get_MissingListing_Returns404WithItemNotFoundError(AdminItemType kind)
    {
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(kind, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((AdminItemRecord?)null);

        var result = await Controller(repo.Object).GetItem(kind.ToString().ToLowerInvariant(), Guid.NewGuid(), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Contains("Item not found.", JsonSerializer.Serialize(notFound.Value), StringComparison.Ordinal);
    }

    // Verifies a resolved listing stays readable, because admins audit inactive listings too.
    [Theory]
    [InlineData("MATCHED")]
    [InlineData("RESOLVED")]
    public async Task Get_InactiveListing_Returns200WithStatusPreserved(string status)
    {
        var item = Item(AdminItemType.FOUND, status);
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(AdminItemType.FOUND, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object).GetItem("found", item.Id, CancellationToken.None);

        Assert.Equal(status, Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Status);
    }

    // Confirms the agreed oversight rule: admins can read soft-deleted listings, and the response stays a
    // complete record rather than degrading to a tombstone.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Get_SoftDeletedListing_Returns200WithCompleteOversightView(AdminItemType kind)
    {
        var deletedAt = DateTime.UtcNow.AddMinutes(-5);
        var item = Item(kind, ActiveStatus, deletedAt);
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(kind, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object).GetItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        var dto = Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal(kind.ToString(), dto.Type);
        Assert.Equal(OwnerId, dto.UserId);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(ActiveStatus, dto.Status);
        Assert.Equal(item.CreatedAt, dto.CreatedAt);
        Assert.Equal(deletedAt, dto.DeletedAt!.Value);
    }

    // Locks a deleted listing to the same agreed payload keys as a live one, so a future field cannot
    // appear for one state and silently not the other.
    [Fact]
    public async Task Get_SoftDeletedListing_UsesTheSamePayloadShapeAsALiveListing()
    {
        var live = Item(AdminItemType.LOST);
        var deleted = Item(AdminItemType.LOST, ActiveStatus, DateTime.UtcNow.AddMinutes(-5));

        var liveRepo = new Mock<IAdminItemsRepository>();
        liveRepo.Setup(x => x.GetByIdAsync(AdminItemType.LOST, live.Id, It.IsAny<CancellationToken>())).ReturnsAsync(live);
        var deletedRepo = new Mock<IAdminItemsRepository>();
        deletedRepo.Setup(x => x.GetByIdAsync(AdminItemType.LOST, deleted.Id, It.IsAny<CancellationToken>())).ReturnsAsync(deleted);

        var liveResult = await Controller(liveRepo.Object).GetItem("lost", live.Id, CancellationToken.None);
        var deletedResult = await Controller(deletedRepo.Object).GetItem("lost", deleted.Id, CancellationToken.None);

        var liveKeys = PayloadKeys(Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(liveResult.Result).Value));
        var deletedKeys = PayloadKeys(Assert.IsType<AdminItemDto>(Assert.IsType<OkObjectResult>(deletedResult.Result).Value));

        Assert.Equal(liveKeys, deletedKeys);
    }

    // Verifies an admin may delete any owner's listing, not just their own.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Delete_ActiveListing_Returns200WithDeletedMessage(AdminItemType kind)
    {
        var item = Item(kind);
        var repo = ActiveDeleteRepo(item);

        var result = await Controller(repo.Object).DeleteItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Item deleted.", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
        repo.Verify(x => x.SoftDeleteActiveAsync(kind, item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Verifies a successful admin delete announces the owner's listing so downstream matches can be retired.
    [Theory]
    [InlineData(AdminItemType.LOST, "LOST")]
    [InlineData(AdminItemType.FOUND, "FOUND")]
    public async Task Delete_ActiveListing_PublishesDeleteRequestWithOwnerItemAndType(AdminItemType kind, string wireType)
    {
        var item = Item(kind);
        var repo = ActiveDeleteRepo(item);
        var publisher = new Mock<IEventPublisher>();
        ItemDeleteRequestedEvent? published = null;
        publisher.Setup(x => x.PublishAsync("items.item.delete_requested", It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, ItemDeleteRequestedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        await Controller(repo.Object, publisher.Object).DeleteItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal(item.Id, published!.ItemId);
        Assert.Equal(OwnerId, published.UserId);
        Assert.Equal(wireType, published.ItemType);
        Assert.Equal("item.delete_requested", published.EventType);
    }

    // Verifies an already-deleted listing is not re-deleted and does not re-announce the deletion. The 404 is
    // a deliberate decision: reading a deleted listing (oversight) and deleting it (gone) differ on purpose,
    // unlike GET, which exposes soft-deleted rows. 
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Delete_AlreadyDeletedListing_Returns404WithoutSoftDeleteOrEvent(AdminItemType kind)
    {
        var item = Item(kind, ActiveStatus, DateTime.UtcNow.AddMinutes(-5));
        var repo = new Mock<IAdminItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(kind, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object, publisher.Object).DeleteItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        repo.Verify(x => x.SoftDeleteActiveAsync(It.IsAny<AdminItemType>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies an unknown listing returns 404 without touching the database or announcing anything.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Delete_MissingListing_Returns404WithoutSoftDeleteOrEvent(AdminItemType kind)
    {
        var repo = new Mock<IAdminItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(kind, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((AdminItemRecord?)null);

        var result = await Controller(repo.Object, publisher.Object).DeleteItem(kind.ToString().ToLowerInvariant(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        repo.Verify(x => x.SoftDeleteActiveAsync(It.IsAny<AdminItemType>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies a matched or resolved listing is protected, and the admin is told which status blocks it.
    [Theory]
    [InlineData("MATCHED")]
    [InlineData("RESOLVED")]
    public async Task Delete_InactiveListing_Returns409NamingTheStatusWithoutSoftDeleteOrEvent(string status)
    {
        var item = Item(AdminItemType.FOUND, status);
        var repo = new Mock<IAdminItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(AdminItemType.FOUND, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await Controller(repo.Object, publisher.Object).DeleteItem("found", item.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = JsonSerializer.Serialize(conflict.Value);
        Assert.Contains(status, body, StringComparison.Ordinal);
        repo.Verify(x => x.SoftDeleteActiveAsync(It.IsAny<AdminItemType>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies losing the race between read and update asks the admin to reload instead of claiming success.
    [Theory]
    [InlineData(AdminItemType.LOST)]
    [InlineData(AdminItemType.FOUND)]
    public async Task Delete_ListingChangedConcurrently_Returns409WithoutPublishing(AdminItemType kind)
    {
        var item = Item(kind);
        var repo = new Mock<IAdminItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(kind, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        repo.Setup(x => x.SoftDeleteActiveAsync(kind, item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await Controller(repo.Object, publisher.Object).DeleteItem(kind.ToString().ToLowerInvariant(), item.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("changed while it was being deleted", JsonSerializer.Serialize(conflict.Value), StringComparison.Ordinal);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies a persistence failure never announces a deletion that did not happen.
    [Fact]
    public async Task Delete_PersistenceFailure_DoesNotPublishDeleteRequest()
    {
        var item = Item(AdminItemType.LOST);
        var repo = new Mock<IAdminItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(AdminItemType.LOST, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        repo.Setup(x => x.SoftDeleteActiveAsync(AdminItemType.LOST, item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Controller(repo.Object, publisher.Object).DeleteItem("lost", item.Id, CancellationToken.None));

        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Locks the policy name and route constraint, so neither can change silently beneath the admin tests.
    [Fact]
    public void BothActionsAreGuardedByAdminOnlyAndRoutedThroughTheConstrainedSegment()
    {
        var authorize = typeof(AdminItemsController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(AuthorizationPolicies.AdminOnly, authorize!.Policy);

        const string expectedRoute = "{type:regex(^(lost|found)$)}/{id:guid}";
        var get = typeof(AdminItemsController).GetMethod(nameof(AdminItemsController.GetItem));
        var delete = typeof(AdminItemsController).GetMethod(nameof(AdminItemsController.DeleteItem));

        Assert.Equal(expectedRoute, get!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal(expectedRoute, delete!.GetCustomAttribute<HttpDeleteAttribute>()!.Template);

        // No method-level override, so the class-level AdminOnly policy governs both actions.
        Assert.Null(get!.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(delete!.GetCustomAttribute<AuthorizeAttribute>());
    }

    private static string[] PayloadKeys(AdminItemDto dto)
    {
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static Mock<IAdminItemsRepository> ActiveDeleteRepo(AdminItemRecord item)
    {
        var repo = new Mock<IAdminItemsRepository>();
        repo.Setup(x => x.GetByIdAsync(item.Type, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        repo.Setup(x => x.SoftDeleteActiveAsync(item.Type, item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return repo;
    }

    private static AdminItemsController Controller(IAdminItemsRepository repo, IEventPublisher? publisher = null)
    {
        var controller = new AdminItemsController(
            repo,
            publisher ?? Mock.Of<IEventPublisher>(),
            Options.Create(new KafkaSettings { TopicPrefix = "items" }),
            Mock.Of<ILogger<AdminItemsController>>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, AdminId.ToString())],
                        "Test"))
            }
        };

        return controller;
    }

    private static AdminItemRecord Item(AdminItemType kind, string status = ActiveStatus, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = kind,
        UserId = OwnerId,
        Title = "Black leather wallet",
        Description = "Lost near the library entrance",
        Status = status,
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        DeletedAt = deletedAt
    };
}