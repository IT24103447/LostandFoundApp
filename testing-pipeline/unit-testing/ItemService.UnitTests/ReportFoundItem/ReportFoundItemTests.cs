using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.ReportFoundItem;

public class ReportFoundItemTests
{
    [Fact]
    public async Task ValidReport_PersistsActiveItemAndPublishesFullPrivateEvent()
    {
        var context = new ItemControllerTestContext();
        var userId = Guid.NewGuid();
        var request = ItemControllerTestContext.FoundRequest();
        var started = DateTime.UtcNow;
        FoundItem? saved = null;
        FoundItemCreatedEvent? published = null;
        var steps = new List<string>();
        context.FoundItems.Setup(x => x.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()))
            .Callback<FoundItem, CancellationToken>((item, _) => { saved = item; steps.Add("create"); })
            .Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.found_item.created", It.IsAny<FoundItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemCreatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.FoundController(userId).ReportFoundItem(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<FoundItemResponseDto>(created.Value);
        Assert.Equal(new[] { "create", "publish" }, steps);
        Assert.NotNull(saved);
        Assert.NotNull(published);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(saved.Id, dto.Id);
        Assert.Equal(userId, saved.UserId);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Equal(FoundItemStatus.ACTIVE, saved.Status);
        Assert.Equal(request.HiddenInformation, saved.HiddenInformation);
        Assert.InRange(saved.CreatedAt, started, DateTime.UtcNow);
        Assert.Equal(saved.CreatedAt, dto.CreatedAt);
        Assert.Equal(saved.Id, published!.FoundItemId);
        Assert.Equal(userId, published.UserId);
        Assert.Equal(saved.Title, published.Title);
        Assert.Equal(saved.Category, published.Category);
        Assert.Equal(saved.Description, published.Description);
        Assert.Equal(saved.DateFound, published.DateFound);
        Assert.Equal(saved.LocationFound, published.LocationFound);
        Assert.Equal(saved.HiddenInformation, published.HiddenInformation);
        Assert.Equal("ACTIVE", published.Status);
        Assert.Equal(saved.CreatedAt, published.CreatedAt);
        Assert.Empty(published.PhotoUrls);
        Assert.DoesNotContain(request.HiddenInformation, JsonSerializer.Serialize(dto));
        Assert.DoesNotContain(typeof(FoundItemResponseDto).GetProperties(), p => p.Name == "HiddenInformation");
        context.Photos.VerifyNoOtherCalls();
        context.FoundItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Fact]
    public async Task OptionalPhoto_IsStoredLinkedAndIncludedInEventAndResponse()
    {
        var context = new ItemControllerTestContext();
        var photo = ItemControllerTestContext.JpegPhoto();
        var request = ItemControllerTestContext.FoundRequest();
        request.Photos = [photo];
        FoundItem? saved = null;
        FoundItemCreatedEvent? published = null;
        var steps = new List<string>();
        const string url = "/photos/found.jpg";
        context.FoundItems.Setup(x => x.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()))
            .Callback<FoundItem, CancellationToken>((item, _) => { saved = item; steps.Add("create"); })
            .Returns(Task.CompletedTask);
        context.Photos.Setup(x => x.SaveAsync(It.IsAny<Guid>(), photo, It.IsAny<CancellationToken>()))
            .Callback<Guid, IFormFile, CancellationToken>((id, file, token) => { Assert.Equal(saved!.Id, id); steps.Add("store"); })
            .ReturnsAsync(url);
        context.FoundItems.Setup(x => x.AddPhotoAsync(It.IsAny<Guid>(), url, It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((id, photoUrl, token) => { Assert.Equal(saved!.Id, id); steps.Add("link"); })
            .ReturnsAsync(() => new FoundItemPhoto { Id = Guid.NewGuid(), FoundItemId = saved!.Id, Url = url });
        context.Publisher.Setup(x => x.PublishAsync("items.found_item.created", It.IsAny<FoundItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemCreatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.FoundController(Guid.NewGuid()).ReportFoundItem(request, CancellationToken.None);

        var dto = Assert.IsType<FoundItemResponseDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(new[] { "create", "store", "link", "publish" }, steps);
        Assert.Single(saved!.Photos);
        Assert.Equal(new[] { url }, dto.PhotoUrls);
        Assert.Equal(url, dto.Photo!.Url);
        Assert.Equal(new[] { url }, published!.PhotoUrls);
        context.FoundItems.VerifyAll();
        context.Photos.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Category")]
    [InlineData("Description")]
    [InlineData("DateFound")]
    [InlineData("LocationFound")]
    [InlineData("HiddenInformation")]
    public async Task MissingRequiredField_ReturnsValidationErrorWithoutWriting(string field)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.FoundRequest();
        SetField(request, field, " ");

        var result = await context.FoundController(Guid.NewGuid()).ReportFoundItem(request, CancellationToken.None);

        Assert.Contains(field, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.FoundItems.VerifyNoOtherCalls();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Category", "Unsupported")]
    [InlineData("DateFound", "14/10/2026")]
    public async Task InvalidCategoryOrDate_ReturnsValidationError(string field, string value)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.FoundRequest();
        SetField(request, field, value);
        var result = await context.FoundController(Guid.NewGuid()).ReportFoundItem(request, CancellationToken.None);
        Assert.Contains(field, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FutureDate_IsRejectedWithoutPersistence()
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.FoundRequest();
        request.DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");
        var result = await context.FoundController(Guid.NewGuid()).ReportFoundItem(request, CancellationToken.None);
        Assert.Contains("DateFound", ItemControllerTestContext.Problem(result).Errors.Keys);
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Title", 151)]
    [InlineData("Category", 51)]
    [InlineData("Description", 2001)]
    [InlineData("LocationFound", 256)]
    [InlineData("HiddenInformation", 501)]
    public void LengthLimits_AreDeclaredOnTheRequestDto(string field, int length)
    {
        var request = ItemControllerTestContext.FoundRequest();
        SetField(request, field, new string('x', length));
        Assert.Contains(ItemControllerTestContext.ValidationErrors(request), e => e.MemberNames.Contains(field));
    }

    [Theory]
    [InlineData("too-many")]
    [InlineData("spoofed")]
    [InlineData("unsupported")]
    public async Task InvalidPhotos_AreRejectedBeforePersistence(string kind)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.FoundRequest();
        request.Photos = kind switch
        {
            "too-many" => [ItemControllerTestContext.JpegPhoto(), ItemControllerTestContext.JpegPhoto()],
            "spoofed" => [ItemControllerTestContext.Photo([0x01, 0x02, 0x03, 0x04], "image/jpeg")],
            _ => [ItemControllerTestContext.Photo([0x01], "application/pdf")]
        };
        var result = await context.FoundController(Guid.NewGuid()).ReportFoundItem(request, CancellationToken.None);
        Assert.Contains("Photos", ItemControllerTestContext.Problem(result).Errors.Keys);
        context.FoundItems.VerifyNoOtherCalls();
        context.Photos.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingUserClaim_CannotCreateReport()
    {
        var context = new ItemControllerTestContext();
        var result = await context.FoundController(null).ReportFoundItem(ItemControllerTestContext.FoundRequest(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        context.FoundItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicRead_DoesNotExposeStoredHiddenInformation()
    {
        var context = new ItemControllerTestContext();
        var item = new FoundItem
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Keys", Category = "Keys",
            Description = "Near bus stop", DateFound = DateOnly.FromDateTime(DateTime.UtcNow),
            LocationFound = "Bus stop", HiddenInformation = "Never show this secret"
        };
        context.FoundItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await context.FoundController(Guid.NewGuid()).GetById(item.Id, CancellationToken.None);

        var dto = Assert.IsType<FoundItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(dto));
        context.FoundItems.VerifyAll();
    }

    private static void SetField(ReportFoundItemRequest request, string field, string value) =>
        typeof(ReportFoundItemRequest).GetProperty(field)!.SetValue(request, value);
}
