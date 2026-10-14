using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.EditItemReport;

public class EditItemReportTests
{
    [Fact]
    public async Task OwnerUpdatesLostReport_ThenPublishesCompleteCurrentState()
    {
        var owner = Guid.NewGuid();
        var item = LostItemFor(owner);
        var before = DateTime.UtcNow;
        var request = ItemControllerTestContext.LostUpdate();
        var context = new ItemControllerTestContext();
        var ct = new CancellationTokenSource().Token;
        LostItemUpdatedEvent? published = null;
        var steps = new List<string>();
        context.LostItems.Setup(x => x.GetByIdAsync(item.Id, ct)).ReturnsAsync(item);
        context.LostItems.Setup(x => x.UpdateAsync(item, ct))
            .Callback<LostItem, CancellationToken>((savedItem, token) => steps.Add("save"))
            .Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.lost_item.updated", It.IsAny<LostItemUpdatedEvent>(), ct))
            .Callback<string, LostItemUpdatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.LostController(owner).UpdateLostItem(item.Id, request, ct);

        var dto = Assert.IsType<LostItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "save", "publish" }, steps);
        Assert.Equal(request.Title, item.Title);
        Assert.Equal(request.Category, item.Category);
        Assert.Equal(request.Description, item.Description);
        Assert.Equal(DateOnly.Parse(request.DateLost), item.DateLost);
        Assert.Equal(request.LastKnownLocation, item.LastKnownLocation);
        Assert.Equal(request.HiddenInformation, item.HiddenInformation);
        Assert.InRange(item.UpdatedAt, before, DateTime.UtcNow);
        Assert.NotNull(published);
        Assert.Equal(owner, published!.UserId);
        Assert.Equal(item.Id, published.LostItemId);
        Assert.Equal(item.Title, published.Title);
        Assert.Equal(item.Category, published.Category);
        Assert.Equal(item.Description, published.Description);
        Assert.Equal(item.DateLost, published.DateLost);
        Assert.Equal(item.LastKnownLocation, published.LastKnownLocation);
        Assert.Equal(item.HiddenInformation, published.HiddenInformation);
        Assert.Equal(item.Status.ToString(), published.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
        Assert.Equal(item.UpdatedAt, published.UpdatedAt);
        Assert.Equal(request.Title, dto.Title);
        Assert.DoesNotContain(request.HiddenInformation, JsonSerializer.Serialize(dto));
        context.LostItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Fact]
    public async Task OwnerUpdatesFoundReport_ThenPublishesCompleteCurrentState()
    {
        var owner = Guid.NewGuid();
        var item = FoundItemFor(owner);
        var before = DateTime.UtcNow;
        var request = ItemControllerTestContext.FoundUpdate();
        var context = new ItemControllerTestContext();
        FoundItemUpdatedEvent? published = null;
        var steps = new List<string>();
        context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        context.FoundItems.Setup(x => x.UpdateAsync(item, It.IsAny<CancellationToken>()))
            .Callback<FoundItem, CancellationToken>((savedItem, token) => steps.Add("save"))
            .Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.found_item.updated", It.IsAny<FoundItemUpdatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemUpdatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.FoundController(owner).UpdateFoundItem(item.Id, request, CancellationToken.None);

        var dto = Assert.IsType<FoundItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { "save", "publish" }, steps);
        Assert.Equal(request.Title, item.Title);
        Assert.Equal(request.Category, item.Category);
        Assert.Equal(request.Description, item.Description);
        Assert.Equal(DateOnly.Parse(request.DateFound), item.DateFound);
        Assert.Equal(request.LocationFound, item.LocationFound);
        Assert.Equal(request.HiddenInformation, item.HiddenInformation);
        Assert.InRange(item.UpdatedAt, before, DateTime.UtcNow);
        Assert.NotNull(published);
        Assert.Equal(owner, published!.UserId);
        Assert.Equal(item.Id, published.FoundItemId);
        Assert.Equal(item.Title, published.Title);
        Assert.Equal(item.Category, published.Category);
        Assert.Equal(item.Description, published.Description);
        Assert.Equal(item.DateFound, published.DateFound);
        Assert.Equal(item.LocationFound, published.LocationFound);
        Assert.Equal(item.HiddenInformation, published.HiddenInformation);
        Assert.Equal(item.Status.ToString(), published.Status);
        Assert.Equal(item.Photos.Select(p => p.Url), published.PhotoUrls);
        Assert.Equal(item.UpdatedAt, published.UpdatedAt);
        Assert.Equal(request.Title, dto.Title);
        Assert.DoesNotContain(request.HiddenInformation, JsonSerializer.Serialize(dto));
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task NonOwnerEdit_Returns403WithoutSavingOrPublishing(string kind)
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var item = LostItemFor(owner);
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.LostController(other).UpdateLostItem(item.Id, ItemControllerTestContext.LostUpdate(), CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.Equal("Old lost title", item.Title);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = FoundItemFor(owner);
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.FoundController(other).UpdateFoundItem(item.Id, ItemControllerTestContext.FoundUpdate(), CancellationToken.None);
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            Assert.Equal("Old found title", item.Title);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task InvalidUpdate_ReturnsFieldErrorsWithoutSavingOrPublishing(string kind)
    {
        var owner = Guid.NewGuid();
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var item = LostItemFor(owner);
            var request = ItemControllerTestContext.LostUpdate();
            request.Title = " ";
            request.Category = "Unsupported";
            request.HiddenInformation = "";
            request.DateLost = "not-a-date";
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.LostController(owner).UpdateLostItem(item.Id, request, CancellationToken.None);
            var errors = ItemControllerTestContext.Problem(result).Errors;
            Assert.Contains("Title", errors.Keys);
            Assert.Contains("Category", errors.Keys);
            Assert.Contains("HiddenInformation", errors.Keys);
            Assert.Contains("DateLost", errors.Keys);
            Assert.Equal("Old lost title", item.Title);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = FoundItemFor(owner);
            var request = ItemControllerTestContext.FoundUpdate();
            request.Description = " ";
            request.LocationFound = " ";
            request.DateFound = "not-a-date";
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.FoundController(owner).UpdateFoundItem(item.Id, request, CancellationToken.None);
            var errors = ItemControllerTestContext.Problem(result).Errors;
            Assert.Contains("Description", errors.Keys);
            Assert.Contains("LocationFound", errors.Keys);
            Assert.Contains("DateFound", errors.Keys);
            Assert.Equal("Old found title", item.Title);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task UnknownReport_Returns404(string kind)
    {
        var context = new ItemControllerTestContext();
        var id = Guid.NewGuid();
        if (kind == "lost")
        {
            context.LostItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            var result = await context.LostController(Guid.NewGuid()).UpdateLostItem(id, ItemControllerTestContext.LostUpdate(), CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.LostItems.VerifyAll();
        }
        else
        {
            context.FoundItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);
            var result = await context.FoundController(Guid.NewGuid()).UpdateFoundItem(id, ItemControllerTestContext.FoundUpdate(), CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.FoundItems.VerifyAll();
        }
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task MissingIdentity_Returns401WithoutRepositoryAccess(string kind)
    {
        var context = new ItemControllerTestContext();
        if (kind == "lost")
        {
            var result = await context.LostController(null).UpdateLostItem(Guid.NewGuid(), ItemControllerTestContext.LostUpdate(), CancellationToken.None);
            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }
        else
        {
            var result = await context.FoundController(null).UpdateFoundItem(Guid.NewGuid(), ItemControllerTestContext.FoundUpdate(), CancellationToken.None);
            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }
        context.LostItems.VerifyNoOtherCalls();
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lost", "Title", 151)]
    [InlineData("lost", "HiddenInformation", 501)]
    [InlineData("found", "Description", 2001)]
    [InlineData("found", "LocationFound", 256)]
    public void OverlongUpdateFields_AreRejectedByDtoValidation(string kind, string field, int length)
    {
        var request = kind == "lost" ? (object)ItemControllerTestContext.LostUpdate() : ItemControllerTestContext.FoundUpdate();
        request.GetType().GetProperty(field)!.SetValue(request, new string('x', length));
        Assert.Contains(ItemControllerTestContext.ValidationErrors(request), e => e.MemberNames.Contains(field));
    }

    private static LostItem LostItemFor(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Old lost title", Category = "Bags",
        Description = "Old description", DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
        LastKnownLocation = "Old place", HiddenInformation = "Old secret", Status = LostItemStatus.ACTIVE,
        CreatedAt = DateTime.UtcNow.AddDays(-3), UpdatedAt = DateTime.UtcNow.AddDays(-3),
        Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/old-lost.jpg" }]
    };

    private static FoundItem FoundItemFor(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Old found title", Category = "Keys",
        Description = "Old description", DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)),
        LocationFound = "Old place", HiddenInformation = "Old secret", Status = FoundItemStatus.ACTIVE,
        CreatedAt = DateTime.UtcNow.AddDays(-3), UpdatedAt = DateTime.UtcNow.AddDays(-3),
        Photos = [new FoundItemPhoto { Id = Guid.NewGuid(), Url = "/photos/old-found.jpg" }]
    };
}
