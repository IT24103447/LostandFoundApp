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

public class EditItemReportStoryTests
{
    private static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task UpdateLostItem_OwnerWithValidPayload_PersistsAndPublishesCompletePrivateEvent()
    {
        var item = LostItemFor(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        LostItemUpdatedEvent? published = null;
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(p => p.PublishAsync("items.lost_item.updated", It.IsAny<LostItemUpdatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, LostItemUpdatedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        var result = await BuildLostController(repo, publisher, OwnerId).UpdateLostItem(item.Id, ValidLostUpdate(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<LostItemResponseDto>(ok.Value);
        Assert.Equal("Corrected lost title", response.Title);
        Assert.DoesNotContain("changed private lost detail", System.Text.Json.JsonSerializer.Serialize(response));
        repo.Verify(r => r.UpdateAsync(item, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(item.Id, published!.LostItemId);
        Assert.Equal("changed private lost detail", published.HiddenInformation);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
    }

    [Fact]
    public async Task UpdateFoundItem_OwnerWithValidPayload_PersistsAndPublishesCompletePrivateEvent()
    {
        var item = FoundItemFor(OwnerId);
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        FoundItemUpdatedEvent? published = null;
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(p => p.PublishAsync("items.found_item.updated", It.IsAny<FoundItemUpdatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemUpdatedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        var result = await BuildFoundController(repo, publisher, OwnerId).UpdateFoundItem(item.Id, ValidFoundUpdate(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<FoundItemResponseDto>(ok.Value);
        Assert.Equal("Corrected found title", response.Title);
        Assert.DoesNotContain("changed private found detail", System.Text.Json.JsonSerializer.Serialize(response));
        repo.Verify(r => r.UpdateAsync(item, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(published);
        Assert.Equal(item.Id, published!.FoundItemId);
        Assert.Equal("changed private found detail", published.HiddenInformation);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
    }

    [Fact]
    public async Task UpdateLostItem_NonOwner_ReturnsForbiddenWithoutSavingOrPublishing()
    {
        var item = LostItemFor(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await BuildLostController(repo, publisher, OtherUserId).UpdateLostItem(item.Id, ValidLostUpdate(), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        repo.Verify(r => r.UpdateAsync(It.IsAny<LostItem>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("Original lost title", item.Title);
    }

    [Fact]
    public async Task UpdateFoundItem_NonOwner_ReturnsForbiddenWithoutSavingOrPublishing()
    {
        var item = FoundItemFor(OwnerId);
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await BuildFoundController(repo, publisher, OtherUserId).UpdateFoundItem(item.Id, ValidFoundUpdate(), CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        repo.Verify(r => r.UpdateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("Original found title", item.Title);
    }

    [Fact]
    public async Task UpdateLostItem_InvalidPayload_ReturnsAllRelevantValidationErrorsAndDoesNotPersist()
    {
        var item = LostItemFor(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var invalid = ValidLostUpdate();
        invalid.Title = " ";
        invalid.Category = "unsupported";
        invalid.DateLost = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        invalid.HiddenInformation = string.Empty;

        var result = await BuildLostController(repo, publisher, OwnerId).UpdateLostItem(item.Id, invalid, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Title", problem.Errors.Keys);
        Assert.Contains("Category", problem.Errors.Keys);
        Assert.Contains("DateLost", problem.Errors.Keys);
        Assert.Contains("HiddenInformation", problem.Errors.Keys);
        repo.Verify(r => r.UpdateAsync(It.IsAny<LostItem>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateFoundItem_InvalidPayload_ReturnsValidationProblemAndDoesNotPersist()
    {
        var item = FoundItemFor(OwnerId);
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var invalid = ValidFoundUpdate();
        invalid.LocationFound = "";
        invalid.DateFound = "12/09/2026";

        var result = await BuildFoundController(repo, publisher, OwnerId).UpdateFoundItem(item.Id, invalid, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("LocationFound", problem.Errors.Keys);
        Assert.Contains("DateFound", problem.Errors.Keys);
        repo.Verify(r => r.UpdateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task UpdateItem_NotFound_Returns404(string kind)
    {
        if (kind == "lost")
        {
            var repo = new Mock<ILostItemsRepository>();
            repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            var result = await BuildLostController(repo, new Mock<IEventPublisher>(), OwnerId)
                .UpdateLostItem(Guid.NewGuid(), ValidLostUpdate(), CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
        else
        {
            var repo = new Mock<IFoundItemsRepository>();
            repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
            var result = await BuildFoundController(repo, new Mock<IEventPublisher>(), OwnerId)
                .UpdateFoundItem(Guid.NewGuid(), ValidFoundUpdate(), CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
    }

    [Fact]
    public async Task UpdateLostItem_ResponseAndGetMappingNeverExposeHiddenInformation()
    {
        var item = LostItemFor(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var controller = BuildLostController(repo, new Mock<IEventPublisher>(), OwnerId);
        var update = await controller.UpdateLostItem(item.Id, ValidLostUpdate(), CancellationToken.None);
        var get = await controller.GetById(item.Id, CancellationToken.None);

        var updateJson = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(update.Result).Value);
        var getJson = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(get.Result).Value);
        Assert.DoesNotContain(item.HiddenInformation, updateJson);
        Assert.DoesNotContain(item.HiddenInformation, getJson);
        Assert.DoesNotContain(typeof(LostItemResponseDto).GetProperties(), p => p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Title", "   ", "Title")]
    [InlineData("Category", "not-supported", "Category")]
    [InlineData("Description", "   ", "Description")]
    [InlineData("LastKnownLocation", "   ", "LastKnownLocation")]
    [InlineData("HiddenInformation", "   ", "HiddenInformation")]
    [InlineData("DateLost", "2026/09/01", "DateLost")]
    [InlineData("DateLost", "2999-01-01", "DateLost")]
    public async Task UpdateLostItem_InvalidIndividualField_IsRejectedWithoutSideEffects(string field, string value, string errorKey)
    {
        var item = LostItemFor(OwnerId);
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        var request = ValidLostUpdate();
        typeof(UpdateLostItemRequest).GetProperty(field)!.SetValue(request, value);

        var result = await BuildLostController(repo, publisher, OwnerId).UpdateLostItem(item.Id, request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains(errorKey, problem.Errors.Keys);
        repo.Verify(r => r.UpdateAsync(It.IsAny<LostItem>(), It.IsAny<CancellationToken>()), Times.Never);
        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemUpdatedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLostItem_InvalidSession_ReturnsUnauthorizedBeforeLookingUpItem()
    {
        var repo = new Mock<ILostItemsRepository>();
        var controller = BuildLostController(repo, new Mock<IEventPublisher>(), OwnerId);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await controller.UpdateLostItem(Guid.NewGuid(), ValidLostUpdate(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateFoundItem_InvalidSession_ReturnsUnauthorizedBeforeLookingUpItem()
    {
        var repo = new Mock<IFoundItemsRepository>();
        var controller = BuildFoundController(repo, new Mock<IEventPublisher>(), OwnerId);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await controller.UpdateFoundItem(Guid.NewGuid(), ValidFoundUpdate(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLostItem_KeepsExistingStatusAndPhotoStateInTheKafkaEvent()
    {
        var item = LostItemFor(OwnerId);
        item.Status = LostItemStatus.CLOSED;
        var repo = new Mock<ILostItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        LostItemUpdatedEvent? published = null;
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemUpdatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, LostItemUpdatedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        await BuildLostController(repo, publisher, OwnerId).UpdateLostItem(item.Id, ValidLostUpdate(), CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal("CLOSED", published!.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
    }

    [Fact]
    public async Task UpdateFoundItem_KeepsExistingStatusAndPhotoStateInTheKafkaEvent()
    {
        var item = FoundItemFor(OwnerId);
        item.Status = FoundItemStatus.CLOSED;
        var repo = new Mock<IFoundItemsRepository>();
        var publisher = new Mock<IEventPublisher>();
        FoundItemUpdatedEvent? published = null;
        repo.Setup(r => r.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemUpdatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemUpdatedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        await BuildFoundController(repo, publisher, OwnerId).UpdateFoundItem(item.Id, ValidFoundUpdate(), CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal("CLOSED", published!.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
    }

    private static LostItemsController BuildLostController(Mock<ILostItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var controller = new LostItemsController(repo.Object, new Mock<IPhotoStorageService>().Object, publisher.Object,
            Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), new Mock<ILogger<LostItemsController>>().Object);
        SetUser(controller, userId);
        return controller;
    }

    private static FoundItemsController BuildFoundController(Mock<IFoundItemsRepository> repo, Mock<IEventPublisher> publisher, Guid userId)
    {
        var controller = new FoundItemsController(repo.Object, new Mock<IPhotoStorageService>().Object, publisher.Object,
            Options.Create(new ItemSettings()), Options.Create(new KafkaSettings { TopicPrefix = "items" }), new Mock<ILogger<FoundItemsController>>().Object);
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, Guid userId) => controller.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"))
        }
    };

    private static UpdateLostItemRequest ValidLostUpdate() => new()
    {
        Title = "Corrected lost title", Category = "Accessories", Description = "Corrected lost description.",
        DateLost = DateTime.UtcNow.ToString("yyyy-MM-dd"), LastKnownLocation = "Corrected lost location",
        HiddenInformation = "changed private lost detail"
    };

    private static UpdateFoundItemRequest ValidFoundUpdate() => new()
    {
        Title = "Corrected found title", Category = "Accessories", Description = "Corrected found description.",
        DateFound = DateTime.UtcNow.ToString("yyyy-MM-dd"), LocationFound = "Corrected found location",
        HiddenInformation = "changed private found detail"
    };

    private static LostItem LostItemFor(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Original lost title", Category = "Accessories", Description = "Original description",
        DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), LastKnownLocation = "Original location", HiddenInformation = "original private lost detail",
        CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-2), Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/lost.jpg" }]
    };

    private static FoundItem FoundItemFor(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Original found title", Category = "Accessories", Description = "Original description",
        DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), LocationFound = "Original location", HiddenInformation = "original private found detail",
        CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-2), Photos = [new FoundItemPhoto { Id = Guid.NewGuid(), Url = "/photos/found.jpg" }]
    };
}
