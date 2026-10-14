using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.MarkItemResolved;

public class MarkItemResolvedTests
{
    [Fact]
    public async Task OwnerResolvesLostItem_StatusIsSavedBeforeFullEventIsPublished()
    {
        var owner = Guid.NewGuid();
        var item = Lost(owner);
        var context = new ItemControllerTestContext();
        var ct = new CancellationTokenSource().Token;
        var started = DateTime.UtcNow;
        var steps = new List<string>();
        LostItemResolvedEvent? published = null;
        context.LostItems.Setup(x => x.GetByIdAsync(item.Id, ct)).ReturnsAsync(item);
        context.LostItems.Setup(x => x.UpdateStatusAsync(item.Id, LostItemStatus.RESOLVED, It.IsAny<DateTime>(), ct))
            .Callback<Guid, LostItemStatus, DateTime, CancellationToken>((id, status, at, token) =>
            {
                Assert.Equal(item.UpdatedAt, at);
                steps.Add("save");
            }).Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.lost_item.resolved", It.IsAny<LostItemResolvedEvent>(), ct))
            .Callback<string, LostItemResolvedEvent, CancellationToken>((topic, evt, token) =>
            {
                published = evt;
                steps.Add("publish");
            }).Returns(ValueTask.CompletedTask);

        var result = await context.LostController(owner).ResolveLostItem(item.Id, ct);

        var dto = Assert.IsType<LostItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "save", "publish" }, steps);
        Assert.Equal(LostItemStatus.RESOLVED, item.Status);
        Assert.Equal("RESOLVED", dto.Status);
        Assert.InRange(item.UpdatedAt, started, DateTime.UtcNow);
        Assert.NotNull(published);
        Assert.Equal("lost_item.resolved", published!.EventType);
        Assert.Equal(owner, published.UserId);
        Assert.Equal(item.Id, published.LostItemId);
        Assert.Equal(item.Title, published.Title);
        Assert.Equal(item.Category, published.Category);
        Assert.Equal(item.Description, published.Description);
        Assert.Equal(item.DateLost, published.DateLost);
        Assert.Equal(item.LastKnownLocation, published.LastKnownLocation);
        Assert.Equal(item.HiddenInformation, published.HiddenInformation);
        Assert.Equal("RESOLVED", published.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
        Assert.Equal(item.UpdatedAt, published.ResolvedAt);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(dto));
        context.LostItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Fact]
    public async Task OwnerResolvesFoundItem_StatusIsSavedBeforeFullEventIsPublished()
    {
        var owner = Guid.NewGuid();
        var item = Found(owner);
        var context = new ItemControllerTestContext();
        var steps = new List<string>();
        FoundItemResolvedEvent? published = null;
        context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        context.FoundItems.Setup(x => x.UpdateStatusAsync(item.Id, FoundItemStatus.RESOLVED, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, FoundItemStatus, DateTime, CancellationToken>((id, status, at, token) =>
            {
                Assert.Equal(item.UpdatedAt, at);
                steps.Add("save");
            }).Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.found_item.resolved", It.IsAny<FoundItemResolvedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemResolvedEvent, CancellationToken>((topic, evt, token) =>
            {
                published = evt;
                steps.Add("publish");
            }).Returns(ValueTask.CompletedTask);

        var result = await context.FoundController(owner).ResolveFoundItem(item.Id, CancellationToken.None);

        var dto = Assert.IsType<FoundItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "save", "publish" }, steps);
        Assert.Equal(FoundItemStatus.RESOLVED, item.Status);
        Assert.Equal("RESOLVED", dto.Status);
        Assert.NotNull(published);
        Assert.Equal("found_item.resolved", published!.EventType);
        Assert.Equal(owner, published.UserId);
        Assert.Equal(item.Id, published.FoundItemId);
        Assert.Equal(item.Title, published.Title);
        Assert.Equal(item.Category, published.Category);
        Assert.Equal(item.Description, published.Description);
        Assert.Equal(item.DateFound, published.DateFound);
        Assert.Equal(item.LocationFound, published.LocationFound);
        Assert.Equal(item.HiddenInformation, published.HiddenInformation);
        Assert.Equal("RESOLVED", published.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
        Assert.Equal(item.UpdatedAt, published.ResolvedAt);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(dto));
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task NonOwnerCannotResolveOrPublish(string kind)
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var item = Lost(owner);
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.LostController(other).ResolveLostItem(item.Id, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.Equal(LostItemStatus.ACTIVE, item.Status);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = Found(owner);
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.FoundController(other).ResolveFoundItem(item.Id, CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.Equal(FoundItemStatus.ACTIVE, item.Status);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MissingItem_Returns404WithoutPublishing(string kind)
    {
        var context = new ItemControllerTestContext();
        var id = Guid.NewGuid();
        if (kind == "lost")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            var result = await context.LostController(Guid.NewGuid()).ResolveLostItem(id, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.LostItems.VerifyAll();
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
            var result = await context.FoundController(Guid.NewGuid()).ResolveFoundItem(id, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task AlreadyResolvedItem_ReturnsConflictWithoutAnotherWrite(string kind)
    {
        var owner = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var item = Lost(owner);
            item.Status = LostItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.LostController(owner).ResolveLostItem(item.Id, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result.Result);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = Found(owner);
            item.Status = FoundItemStatus.RESOLVED;
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.FoundController(owner).ResolveFoundItem(item.Id, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result.Result);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MissingIdentity_Returns401BeforeLookup(string kind)
    {
        var context = new ItemControllerTestContext();
        if (kind == "lost")
            Assert.IsType<UnauthorizedObjectResult>((await context.LostController(null).ResolveLostItem(Guid.NewGuid(), CancellationToken.None)).Result);
        else
            Assert.IsType<UnauthorizedObjectResult>((await context.FoundController(null).ResolveFoundItem(Guid.NewGuid(), CancellationToken.None)).Result);
        context.LostItems.VerifyNoOtherCalls();
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolvedItem_RemainsInOwnersReportHistory(string kind)
    {
        var owner = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var item = Lost(owner);
            item.Status = LostItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByUserIdAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync([item]);
            var result = await context.LostController(owner).GetMine(CancellationToken.None);
            var history = Assert.IsType<List<LostItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(item.Id, Assert.Single(history).Id);
            Assert.Equal("RESOLVED", history[0].Status);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = Found(owner);
            item.Status = FoundItemStatus.RESOLVED;
            context.FoundItems.Setup(x => x.GetByUserIdAsync(owner, It.IsAny<CancellationToken>()))
                .ReturnsAsync([item]);
            var result = await context.FoundController(owner).GetMine(CancellationToken.None);
            var history = Assert.IsType<List<FoundItemResponseDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal(item.Id, Assert.Single(history).Id);
            Assert.Equal("RESOLVED", history[0].Status);
            context.FoundItems.VerifyAll();
        }
    }

    private static LostItem Lost(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Lost wallet", Category = "Accessories",
        Description = "Brown wallet", DateLost = new DateOnly(2026, 9, 10),
        LastKnownLocation = "Library", HiddenInformation = "Blue stitching",
        CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-2),
        Status = LostItemStatus.ACTIVE,
        Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/lost.jpg" }]
    };

    private static FoundItem Found(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Found keys", Category = "Keys",
        Description = "Three keys", DateFound = new DateOnly(2026, 9, 10),
        LocationFound = "Bus stop", HiddenInformation = "Green tag",
        CreatedAt = DateTime.UtcNow.AddDays(-2), UpdatedAt = DateTime.UtcNow.AddDays(-2),
        Status = FoundItemStatus.ACTIVE,
        Photos = [new FoundItemPhoto { Id = Guid.NewGuid(), Url = "/photos/found.jpg" }]
    };
}
