using System.Security.Claims;
using System.Text.Json;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Events;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ItemService.Tests.Controllers;

/// <summary>Story 7 contract tests for secure, auditable report deletion.</summary>
public sealed class DeleteItemReportStoryTests
{
    // Story 7: soft deletion, ownership, match protection, and delete-event ordering.
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NonOwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Verifies an owner soft-deletes a lost report and publishes only the permitted delete-event fields.
    [Fact]
    public async Task DeleteLost_Owner_SoftDeletesAndPublishesIdAndTypeOnlyEvent()
    {
        var item = Lost(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        ItemDeleteRequestedEvent? published = null;
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(x => x.PublishAsync("items.item.delete_requested", It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, ItemDeleteRequestedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        var result = await LostController(repo, publisher, OwnerId).DeleteLostItem(item.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        repo.Verify(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(item.Id, published!.ItemId);
        Assert.Equal("LOST", published.ItemType);
        Assert.Equal(OwnerId, published.UserId);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(published), StringComparison.Ordinal);
    }

    // Verifies an owner soft-deletes a found report and publishes only the permitted delete-event fields.
    [Fact]
    public async Task DeleteFound_Owner_SoftDeletesAndPublishesIdAndTypeOnlyEvent()
    {
        var item = Found(OwnerId);
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        ItemDeleteRequestedEvent? published = null;
        repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(x => x.PublishAsync("items.item.delete_requested", It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, ItemDeleteRequestedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        var result = await FoundController(repo, publisher, OwnerId).DeleteFoundItem(item.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        repo.Verify(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(item.Id, published!.ItemId);
        Assert.Equal("FOUND", published.ItemType);
        Assert.Equal(OwnerId, published.UserId);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(published), StringComparison.Ordinal);
    }

    // Verifies a non-owner receives 403 with no deletion and no event.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_NonOwner_Returns403WithoutSoftDeleteOrEvent(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await LostController(repo, publisher, NonOwnerId).DeleteLostItem(item.Id, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
            repo.Verify(x => x.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await FoundController(repo, publisher, NonOwnerId).DeleteFoundItem(item.Id, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
            repo.Verify(x => x.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies matched reports cannot be deleted and the owner is told to resolve instead.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_MatchedItem_Returns409AndInstructsOwnerToResolve(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); item.Status = LostItemStatus.MATCHED;
            var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await LostController(repo, publisher, OwnerId).DeleteLostItem(item.Id, CancellationToken.None);
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("resolve", JsonSerializer.Serialize(conflict.Value), StringComparison.OrdinalIgnoreCase);
            repo.Verify(x => x.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var item = Found(OwnerId); item.Status = FoundItemStatus.MATCHED;
            var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await FoundController(repo, publisher, OwnerId).DeleteFoundItem(item.Id, CancellationToken.None);
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("resolve", JsonSerializer.Serialize(conflict.Value), StringComparison.OrdinalIgnoreCase);
            repo.Verify(x => x.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies missing reports return 404 without publication.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_MissingItem_Returns404WithoutEvent(string kind)
    {
        var id = Guid.NewGuid();
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            Assert.IsType<NotFoundObjectResult>(await LostController(repo, publisher, OwnerId).DeleteLostItem(id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
            Assert.IsType<NotFoundObjectResult>(await FoundController(repo, publisher, OwnerId).DeleteFoundItem(id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies an invalid session returns 401 before repository access.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_InvalidSession_Returns401BeforeRepositoryLookup(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>();
            var controller = LostController(repo, new Mock<IEventPublisher>(), OwnerId); SetSubject(controller, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>(await controller.DeleteLostItem(Guid.NewGuid(), CancellationToken.None));
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>();
            var controller = FoundController(repo, new Mock<IEventPublisher>(), OwnerId); SetSubject(controller, "not-a-guid");
            Assert.IsType<UnauthorizedObjectResult>(await controller.DeleteFoundItem(Guid.NewGuid(), CancellationToken.None));
            repo.Verify(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies a database failure prevents publishing a deletion event.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_PersistenceFailure_DoesNotPublishEvent(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            repo.Setup(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LostController(repo, publisher, OwnerId).DeleteLostItem(item.Id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            repo.Setup(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FoundController(repo, publisher, OwnerId).DeleteFoundItem(item.Id, CancellationToken.None));
            publisher.Verify(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    // Verifies publisher failure occurs after the soft delete is already persisted.
    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task Delete_PublisherFailure_HappensOnlyAfterSoftDelete(string kind)
    {
        if (kind == "lost")
        {
            var item = Lost(OwnerId); var repo = new Mock<ILostItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("publisher unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => LostController(repo, publisher, OwnerId).DeleteLostItem(item.Id, CancellationToken.None));
            repo.Verify(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            var item = Found(OwnerId); var repo = new Mock<IFoundItemsRepository>(); var publisher = new Mock<IEventPublisher>();
            repo.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            publisher.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<ItemDeleteRequestedEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("publisher unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => FoundController(repo, publisher, OwnerId).DeleteFoundItem(item.Id, CancellationToken.None));
            repo.Verify(x => x.SoftDeleteAsync(item.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    private static LostItemsController LostController(Mock<ILostItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var controller = new LostItemsController(repo.Object, Mock.Of<IPhotoStorageService>(), publisher.Object,
            Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), Mock.Of<ILogger<LostItemsController>>());
        SetUser(controller, userId); return controller;
    }

    private static FoundItemsController FoundController(Mock<IFoundItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var controller = new FoundItemsController(repo.Object, Mock.Of<IPhotoStorageService>(), publisher.Object,
            Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), Mock.Of<ILogger<FoundItemsController>>());
        SetUser(controller, userId); return controller;
    }

    private static void SetUser(ControllerBase controller, Guid userId) => controller.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test")) }
    };

    private static void SetSubject(ControllerBase controller, string subject) => controller.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Test")) }
    };

    private static LostItem Lost(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Lost wallet", Category = "Accessories", Description = "Brown wallet",
        DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LastKnownLocation = "Library", HiddenInformation = "private lost marker",
        CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
    };

    private static FoundItem Found(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Found wallet", Category = "Accessories", Description = "Black wallet",
        DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), LocationFound = "Cafeteria", HiddenInformation = "private found marker",
        CreatedAt = DateTime.UtcNow.AddDays(-1), UpdatedAt = DateTime.UtcNow.AddDays(-1)
    };
}
