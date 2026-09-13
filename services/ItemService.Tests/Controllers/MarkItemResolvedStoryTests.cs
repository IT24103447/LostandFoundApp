using System.Security.Claims;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ItemService.Tests.Controllers;

/// <summary>Story 5 contract tests: resolution is owner-only, durable, and publishes full private state.</summary>
public sealed class MarkItemResolvedStoryTests
{
    // Story 5: resolution changes, authorization, state rules, and complete Kafka resolution events.
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NonOwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Verifies an owner can resolve a lost report and publish its full private event.
    [Fact]
    public async Task ResolveLostItem_Owner_ChangesStatusPersistsAndPublishesFullPrivateEvent()
    {
        var item = Lost(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        LostItemResolvedEvent? evt = null;
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(x => x.PublishAsync("items.lost_item.resolved", It.IsAny<LostItemResolvedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, LostItemResolvedEvent, CancellationToken>((_, value, _) => evt = value).Returns(ValueTask.CompletedTask);

        var result = await LostController(repo, publisher, OwnerId).ResolveLostItem(item.Id, CancellationToken.None);

        var response = Assert.IsType<LostItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("RESOLVED", response.Status);
        Assert.Equal(LostItemStatus.RESOLVED, item.Status);
        repo.Verify(x => x.UpdateStatusAsync(item.Id, LostItemStatus.RESOLVED, item.UpdatedAt, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(evt);
        Assert.Equal(item.Id, evt!.LostItemId); Assert.Equal(OwnerId, evt.UserId);
        Assert.Equal(item.Title, evt.Title); Assert.Equal(item.Category, evt.Category); Assert.Equal(item.Description, evt.Description);
        Assert.Equal(item.DateLost, evt.DateLost); Assert.Equal(item.LastKnownLocation, evt.LastKnownLocation);
        Assert.Equal("RESOLVED", evt.Status); Assert.Equal(item.HiddenInformation, evt.HiddenInformation);
        Assert.Equal(item.Photos.Select(x => x.Url), evt.PhotoUrls); Assert.Equal(item.UpdatedAt, evt.ResolvedAt);
        Assert.DoesNotContain(item.HiddenInformation, System.Text.Json.JsonSerializer.Serialize(response));
        Assert.DoesNotContain(typeof(LostItemResponseDto).GetProperties(), p => p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    // Verifies an owner can resolve a found report and publish its full private event.
    [Fact]
    public async Task ResolveFoundItem_Owner_ChangesStatusPersistsAndPublishesFullPrivateEvent()
    {
        var item = Found(OwnerId);
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        FoundItemResolvedEvent? evt = null;
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(x => x.PublishAsync("items.found_item.resolved", It.IsAny<FoundItemResolvedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemResolvedEvent, CancellationToken>((_, value, _) => evt = value).Returns(ValueTask.CompletedTask);

        var result = await FoundController(repo, publisher, OwnerId).ResolveFoundItem(item.Id, CancellationToken.None);

        var response = Assert.IsType<FoundItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("RESOLVED", response.Status);
        Assert.Equal(FoundItemStatus.RESOLVED, item.Status);
        repo.Verify(x => x.UpdateStatusAsync(item.Id, FoundItemStatus.RESOLVED, item.UpdatedAt, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(evt);
        Assert.Equal(item.Id, evt!.FoundItemId); Assert.Equal(OwnerId, evt.UserId);
        Assert.Equal(item.Title, evt.Title); Assert.Equal(item.Category, evt.Category); Assert.Equal(item.Description, evt.Description);
        Assert.Equal(item.DateFound, evt.DateFound); Assert.Equal(item.LocationFound, evt.LocationFound);
        Assert.Equal("RESOLVED", evt.Status); Assert.Equal(item.HiddenInformation, evt.HiddenInformation);
        Assert.Equal(item.Photos.Select(x => x.Url), evt.PhotoUrls); Assert.Equal(item.UpdatedAt, evt.ResolvedAt);
        Assert.DoesNotContain(item.HiddenInformation, System.Text.Json.JsonSerializer.Serialize(response));
        Assert.DoesNotContain(typeof(FoundItemResponseDto).GetProperties(), p => p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    // Verifies a non-owner cannot resolve a lost report.
    [Fact]
    public async Task ResolveLostItem_NonOwner_Returns403WithoutMutationOrEvent()
    {
        var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var result = await LostController(repo, publisher, NonOwnerId).ResolveLostItem(item.Id, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(LostItemStatus.ACTIVE, item.Status);
        repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<LostItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies a non-owner cannot resolve a found report.
    [Fact]
    public async Task ResolveFoundItem_NonOwner_Returns403WithoutMutationOrEvent()
    {
        var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var result = await FoundController(repo, publisher, NonOwnerId).ResolveFoundItem(item.Id, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(FoundItemStatus.ACTIVE, item.Status);
        repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<FoundItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Verifies unauthenticated resolution is rejected before lookup.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_InvalidSession_Returns401BeforeLookup(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>(); var c = LostController(repo, new Mock<IEventPublisher>(), OwnerId); c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<UnauthorizedObjectResult>((await c.ResolveLostItem(Guid.NewGuid(), CancellationToken.None)).Result);
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>(); var c = FoundController(repo, new Mock<IEventPublisher>(), OwnerId); c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.IsType<UnauthorizedObjectResult>((await c.ResolveFoundItem(Guid.NewGuid(), CancellationToken.None)).Result);
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies missing or already-resolved reports do not cause another write or event.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_MissingOrAlreadyResolved_DoesNotWriteOrPublish(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            Assert.IsType<NotFoundObjectResult>((await LostController(repo, publisher, OwnerId).ResolveLostItem(Guid.NewGuid(), CancellationToken.None)).Result);
            var resolved = Lost(OwnerId); resolved.Status = LostItemStatus.RESOLVED;
            repo.Setup(x => x.GetByIdAsync(resolved.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            Assert.IsType<ConflictObjectResult>((await LostController(repo, publisher, OwnerId).ResolveLostItem(resolved.Id, CancellationToken.None)).Result);
            repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<LostItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
            Assert.IsType<NotFoundObjectResult>((await FoundController(repo, publisher, OwnerId).ResolveFoundItem(Guid.NewGuid(), CancellationToken.None)).Result);
            var resolved = Found(OwnerId); resolved.Status = FoundItemStatus.RESOLVED;
            repo.Setup(x => x.GetByIdAsync(resolved.Id, It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            Assert.IsType<ConflictObjectResult>((await FoundController(repo, publisher, OwnerId).ResolveFoundItem(resolved.Id, CancellationToken.None)).Result);
            repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<FoundItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies malformed JWT subjects are rejected before accessing the report.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_MalformedSubjectClaim_Returns401WithoutLookingUpTheReport(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>(); var c = LostController(repo, new Mock<IEventPublisher>(), OwnerId); SetSubject(c, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>((await c.ResolveLostItem(Guid.NewGuid(), CancellationToken.None)).Result);
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>(); var c = FoundController(repo, new Mock<IEventPublisher>(), OwnerId); SetSubject(c, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>((await c.ResolveFoundItem(Guid.NewGuid(), CancellationToken.None)).Result);
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies closed reports cannot be resolved again.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_ClosedReport_Returns409WithoutSecondWriteOrEvent(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); item.Status = LostItemStatus.CLOSED; var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            Assert.IsType<ConflictObjectResult>((await LostController(repo, publisher, OwnerId).ResolveLostItem(item.Id, CancellationToken.None)).Result);
            Assert.Equal(LostItemStatus.CLOSED, item.Status); repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<LostItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var item = Found(OwnerId); item.Status = FoundItemStatus.CLOSED; var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            Assert.IsType<ConflictObjectResult>((await FoundController(repo, publisher, OwnerId).ResolveFoundItem(item.Id, CancellationToken.None)).Result);
            Assert.Equal(FoundItemStatus.CLOSED, item.Status); repo.Verify(x => x.UpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<FoundItemStatus>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies a failed status persistence prevents event publication.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_PersistenceFailure_DoesNotPublishAnEventForAnUnpersistedChange(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            repo.Setup(x => x.UpdateStatusAsync(item.Id, LostItemStatus.RESOLVED, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LostController(repo, publisher, OwnerId).ResolveLostItem(item.Id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            repo.Setup(x => x.UpdateStatusAsync(item.Id, FoundItemStatus.RESOLVED, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FoundController(repo, publisher, OwnerId).ResolveFoundItem(item.Id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemResolvedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies publisher failure happens only after the resolved status is persisted.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolveItem_PublisherFailure_HappensOnlyAfterTheStatusHasBeenPersisted(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemResolvedEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("publisher unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LostController(repo, publisher, OwnerId).ResolveLostItem(item.Id, CancellationToken.None));
            repo.Verify(x => x.UpdateStatusAsync(item.Id, LostItemStatus.RESOLVED, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemResolvedEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("publisher unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FoundController(repo, publisher, OwnerId).ResolveFoundItem(item.Id, CancellationToken.None));
            repo.Verify(x => x.UpdateStatusAsync(item.Id, FoundItemStatus.RESOLVED, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    private static LostItemsController LostController(Mock<ILostItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var c = new LostItemsController(repo.Object, new Mock<IPhotoStorageService>().Object, publisher.Object, Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), new Mock<ILogger<LostItemsController>>().Object); SetUser(c, userId); return c;
    }
    private static FoundItemsController FoundController(Mock<IFoundItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var c = new FoundItemsController(repo.Object, new Mock<IPhotoStorageService>().Object, publisher.Object, Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), new Mock<ILogger<FoundItemsController>>().Object); SetUser(c, userId); return c;
    }
    private static void SetUser(ControllerBase c, Guid userId) => c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")) } };
    private static void SetSubject(ControllerBase c, string subject) => c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Test")) } };
    private static LostItem Lost(Guid owner) => new() { Id = Guid.NewGuid(), UserId = owner, Title = "Lost wallet", Category = "Accessories", Description = "Brown wallet", DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LastKnownLocation = "Library", HiddenInformation = "private lost marker", CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1), Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/lost.jpg" }] };
    private static FoundItem Found(Guid owner) => new() { Id = Guid.NewGuid(), UserId = owner, Title = "Found wallet", Category = "Accessories", Description = "Black wallet", DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LocationFound = "Cafeteria", HiddenInformation = "private found marker", CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1), Photos = [new FoundItemPhoto { Id = Guid.NewGuid(), Url = "/photos/found.jpg" }] };
}
