using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Events;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.DeleteItemReport;

public class DeleteItemReportTests
{
    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task OwnerDeletesActiveReport_BeforePublishingItsIdAndType(string type)
    {
        var owner = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        var ct = new CancellationTokenSource().Token;
        var steps = new List<string>();
        var started = DateTime.UtcNow;
        DateTime? deletedAt = null;
        ItemDeleteRequestedEvent? published = null;

        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(itemId, ct))
                .ReturnsAsync(new LostItem { Id = itemId, UserId = owner, Status = LostItemStatus.ACTIVE });
            context.LostItems.Setup(x => x.SoftDeleteAsync(itemId, It.IsAny<DateTime>(), ct))
                .Callback<Guid, DateTime, CancellationToken>((_, at, _) =>
                {
                    deletedAt = at;
                    steps.Add("delete");
                }).Returns(Task.CompletedTask);
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(itemId, ct))
                .ReturnsAsync(new FoundItem { Id = itemId, UserId = owner, Status = FoundItemStatus.ACTIVE });
            context.FoundItems.Setup(x => x.SoftDeleteAsync(itemId, It.IsAny<DateTime>(), ct))
                .Callback<Guid, DateTime, CancellationToken>((_, at, _) =>
                {
                    deletedAt = at;
                    steps.Add("delete");
                }).Returns(Task.CompletedTask);
        }

        context.Publisher.Setup(x => x.PublishAsync("items.item.delete_requested", It.IsAny<ItemDeleteRequestedEvent>(), ct))
            .Callback<string, ItemDeleteRequestedEvent, CancellationToken>((_, evt, _) =>
            {
                published = evt;
                steps.Add("publish");
            }).Returns(ValueTask.CompletedTask);

        var result = type == "LOST"
            ? await context.LostController(owner).DeleteLostItem(itemId, ct)
            : await context.FoundController(owner).DeleteFoundItem(itemId, ct);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { "delete", "publish" }, steps);
        Assert.NotNull(deletedAt);
        Assert.InRange(deletedAt.Value, started, DateTime.UtcNow);
        Assert.NotNull(published);
        Assert.Equal("item.delete_requested", published!.EventType);
        Assert.Equal(itemId, published.ItemId);
        Assert.Equal(type, published.ItemType);
        Assert.Equal(owner, published.UserId);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task NonOwnerCannotDeleteOrPublish(string type)
    {
        var owner = Guid.NewGuid();
        var caller = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        IActionResult result;
        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LostItem { Id = itemId, UserId = owner });
            result = await context.LostController(caller).DeleteLostItem(itemId, CancellationToken.None);
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FoundItem { Id = itemId, UserId = owner });
            result = await context.FoundController(caller).DeleteFoundItem(itemId, CancellationToken.None);
        }

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task ConfirmedMatchRequiresResolution_InsteadOfDeletion(string type)
    {
        var owner = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        IActionResult result;
        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LostItem { Id = itemId, UserId = owner, Status = LostItemStatus.MATCHED });
            result = await context.LostController(owner).DeleteLostItem(itemId, CancellationToken.None);
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FoundItem { Id = itemId, UserId = owner, Status = FoundItemStatus.MATCHED });
            result = await context.FoundController(owner).DeleteFoundItem(itemId, CancellationToken.None);
        }

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("resolve", JsonSerializer.Serialize(conflict.Value), StringComparison.OrdinalIgnoreCase);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task MissingReport_Returns404WithoutPublishing(string type)
    {
        var context = new ItemControllerTestContext();
        var itemId = Guid.NewGuid();
        IActionResult result;
        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((LostItem?)null);
            result = await context.LostController(Guid.NewGuid()).DeleteLostItem(itemId, CancellationToken.None);
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((FoundItem?)null);
            result = await context.FoundController(Guid.NewGuid()).DeleteFoundItem(itemId, CancellationToken.None);
        }

        Assert.IsType<NotFoundObjectResult>(result);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task MissingIdentity_Returns401BeforeRepositoryAccess(string type)
    {
        var context = new ItemControllerTestContext();
        var result = type == "LOST"
            ? await context.LostController(null).DeleteLostItem(Guid.NewGuid(), CancellationToken.None)
            : await context.FoundController(null).DeleteFoundItem(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        context.LostItems.VerifyNoOtherCalls();
        context.FoundItems.VerifyNoOtherCalls();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LOST")]
    [InlineData("FOUND")]
    public async Task FailedSoftDelete_DoesNotPublishDeleteEvent(string type)
    {
        var owner = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (type == "LOST")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LostItem { Id = itemId, UserId = owner });
            context.LostItems.Setup(x => x.SoftDeleteAsync(itemId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.LostController(owner).DeleteLostItem(itemId, CancellationToken.None));
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FoundItem { Id = itemId, UserId = owner });
            context.FoundItems.Setup(x => x.SoftDeleteAsync(itemId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Database unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.FoundController(owner).DeleteFoundItem(itemId, CancellationToken.None));
        }

        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenRepositoryFiltersSoftDeletedReport_PublicDetailsReturn404()
    {
        var context = new ItemControllerTestContext();
        var itemId = Guid.NewGuid();
        var caller = Guid.NewGuid();
        context.LostItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LostItem?)null);
        context.FoundItems.Setup(x => x.GetByIdAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FoundItem?)null);

        var result = await context.ItemsController(caller).GetById(itemId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }
}
