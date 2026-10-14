using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.ViewItemDetails;

public class ViewItemDetailsTests
{
    [Fact]
    public async Task LostItemDetails_ReturnAllPublicFieldsWithoutHiddenInformation()
    {
        var context = new ItemControllerTestContext();
        var item = Lost(Guid.NewGuid());
        var ct = new CancellationTokenSource().Token;
        context.LostItems.Setup(x => x.GetByIdAsync(item.Id, ct)).ReturnsAsync(item);

        var result = await context.ItemsController(Guid.NewGuid()).GetById(item.Id, ct);

        var dto = Ok(result);
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal("LOST", dto.Type);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(item.Category, dto.Category);
        Assert.Equal(item.LastKnownLocation, dto.Location);
        Assert.Equal(item.DateLost.ToString("yyyy-MM-dd"), dto.Date);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Equal(item.CreatedAt, dto.CreatedAt);
        Assert.Equal(item.Photos.Select(p => p.Url), dto.PhotoUrls);
        Assert.Equal(item.Photos[0].Id, dto.Photo!.Id);
        Assert.Equal(item.Photos[0].Url, dto.Photo.Url);
        AssertPrivateFieldAbsent(dto, item.HiddenInformation);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FoundItemDetails_ReturnAllPublicFieldsWithoutHiddenInformation()
    {
        var context = new ItemControllerTestContext();
        var item = Found(Guid.NewGuid());
        context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
        context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await context.ItemsController(Guid.NewGuid()).GetById(item.Id, CancellationToken.None);

        var dto = Ok(result);
        Assert.Equal(item.Id, dto.Id);
        Assert.Equal("FOUND", dto.Type);
        Assert.Equal(item.Title, dto.Title);
        Assert.Equal(item.Description, dto.Description);
        Assert.Equal(item.Category, dto.Category);
        Assert.Equal(item.LocationFound, dto.Location);
        Assert.Equal(item.DateFound.ToString("yyyy-MM-dd"), dto.Date);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Equal(item.CreatedAt, dto.CreatedAt);
        Assert.Empty(dto.PhotoUrls);
        Assert.Null(dto.Photo);
        AssertPrivateFieldAbsent(dto, item.HiddenInformation);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownOrRepositoryFilteredDeletedItem_Returns404(bool emptyId)
    {
        var context = new ItemControllerTestContext();
        var id = emptyId ? Guid.Empty : Guid.NewGuid();
        context.LostItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
        context.FoundItems.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync((FoundItem?)null);

        var result = await context.ItemsController(Guid.NewGuid()).GetById(id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        context.LostItems.VerifyAll();
        context.FoundItems.VerifyAll();
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolvedItem_Is404ForNonOwner(string kind)
    {
        var context = new ItemControllerTestContext();
        var owner = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        if (kind == "lost")
        {
            var item = Lost(owner);
            item.Status = LostItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.ItemsController(viewer).GetById(item.Id, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.LostItems.VerifyAll();
            context.FoundItems.VerifyNoOtherCalls();
        }
        else
        {
            var item = Found(owner);
            item.Status = FoundItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.ItemsController(viewer).GetById(item.Id, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result.Result);
            context.LostItems.VerifyAll();
            context.FoundItems.VerifyAll();
        }
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("found")]
    public async Task ResolvedItem_RemainsVisibleToOriginalReporter(string kind)
    {
        var context = new ItemControllerTestContext();
        var owner = Guid.NewGuid();
        if (kind == "lost")
        {
            var item = Lost(owner);
            item.Status = LostItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.ItemsController(owner).GetById(item.Id, CancellationToken.None);
            Assert.Equal("RESOLVED", Ok(result).Status);
            context.LostItems.VerifyAll();
        }
        else
        {
            var item = Found(owner);
            item.Status = FoundItemStatus.RESOLVED;
            context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync((LostItem?)null);
            context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
            var result = await context.ItemsController(owner).GetById(item.Id, CancellationToken.None);
            Assert.Equal("RESOLVED", Ok(result).Status);
            context.LostItems.VerifyAll();
            context.FoundItems.VerifyAll();
        }
    }

    [Fact]
    public async Task MissingUserIdentity_Returns401BeforeRepositoryLookup()
    {
        var context = new ItemControllerTestContext();
        var result = await context.ItemsController(null).GetById(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        context.LostItems.VerifyNoOtherCalls();
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Fact]
    public void PublicDetailContract_HasNeitherPrivateInformationNorUserId()
    {
        var properties = typeof(ItemDetailDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(properties, p => p.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Contains("User", StringComparison.OrdinalIgnoreCase));
    }

    private static ItemDetailDto Ok(ActionResult<ItemDetailDto> result) =>
        Assert.IsType<ItemDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static void AssertPrivateFieldAbsent(ItemDetailDto dto, string secret)
    {
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("HiddenInformation", json, StringComparison.OrdinalIgnoreCase);
    }

    private static LostItem Lost(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Lost camera", Category = "Electronics",
        Description = "Black camera", DateLost = new DateOnly(2026, 9, 10),
        LastKnownLocation = "Library", HiddenInformation = "Serial number 9876",
        Status = LostItemStatus.ACTIVE, CreatedAt = new DateTime(2026, 9, 11, 8, 0, 0, DateTimeKind.Utc),
        Photos = [new LostItemPhoto { Id = Guid.NewGuid(), Url = "/photos/camera.jpg" }]
    };

    private static FoundItem Found(Guid owner) => new()
    {
        Id = Guid.NewGuid(), UserId = owner, Title = "Found keys", Category = "Keys",
        Description = "Three keys", DateFound = new DateOnly(2026, 9, 12),
        LocationFound = "Bus stop", HiddenInformation = "Green tag",
        Status = FoundItemStatus.ACTIVE, CreatedAt = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc)
    };
}
