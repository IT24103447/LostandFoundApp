using System.Text.Json;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.UnitTests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ItemService.UnitTests.ReportLostItem;

public class ReportLostItemTests
{
    [Fact]
    public async Task ValidReport_PersistsActiveItemAndPublishesFullPrivateEvent()
    {
        var context = new ItemControllerTestContext();
        var userId = Guid.NewGuid();
        var request = ItemControllerTestContext.LostRequest();
        var ct = new CancellationTokenSource().Token;
        var started = DateTime.UtcNow;
        LostItem? saved = null;
        LostItemCreatedEvent? published = null;
        var steps = new List<string>();
        context.LostItems.Setup(x => x.CreateAsync(It.IsAny<LostItem>(), ct))
            .Callback<LostItem, CancellationToken>((item, _) => { saved = item; steps.Add("create"); })
            .Returns(Task.CompletedTask);
        context.Publisher.Setup(x => x.PublishAsync("items.lost_item.created", It.IsAny<LostItemCreatedEvent>(), ct))
            .Callback<string, LostItemCreatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.LostController(userId).ReportLostItem(request, ct);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<LostItemResponseDto>(created.Value);
        Assert.Equal(new[] { "create", "publish" }, steps);
        Assert.NotNull(saved);
        Assert.NotNull(published);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(saved.Id, dto.Id);
        Assert.Equal(userId, saved.UserId);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.Equal(LostItemStatus.ACTIVE, saved.Status);
        Assert.Equal(request.HiddenInformation, saved.HiddenInformation);
        Assert.InRange(saved.CreatedAt, started, DateTime.UtcNow);
        Assert.Equal(saved.CreatedAt, dto.CreatedAt);
        Assert.Equal(saved.Id, published!.LostItemId);
        Assert.Equal(userId, published.UserId);
        Assert.Equal(saved.Title, published.Title);
        Assert.Equal(saved.Category, published.Category);
        Assert.Equal(saved.Description, published.Description);
        Assert.Equal(saved.DateLost, published.DateLost);
        Assert.Equal(saved.LastKnownLocation, published.LastKnownLocation);
        Assert.Equal(saved.HiddenInformation, published.HiddenInformation);
        Assert.Equal("ACTIVE", published.Status);
        Assert.Equal(saved.CreatedAt, published.CreatedAt);
        Assert.Empty(published.PhotoUrls);
        Assert.DoesNotContain(request.HiddenInformation, JsonSerializer.Serialize(dto));
        Assert.DoesNotContain(typeof(LostItemResponseDto).GetProperties(), p => p.Name == "HiddenInformation");
        context.Photos.VerifyNoOtherCalls();
        context.LostItems.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Fact]
    public async Task OptionalPhoto_IsStoredLinkedAndIncludedInEventAndResponse()
    {
        var context = new ItemControllerTestContext();
        var photo = ItemControllerTestContext.JpegPhoto();
        var request = ItemControllerTestContext.LostRequest();
        request.Photos = [photo];
        LostItem? saved = null;
        LostItemCreatedEvent? published = null;
        var steps = new List<string>();
        const string url = "/photos/lost.jpg";
        context.LostItems.Setup(x => x.CreateAsync(It.IsAny<LostItem>(), It.IsAny<CancellationToken>()))
            .Callback<LostItem, CancellationToken>((item, _) => { saved = item; steps.Add("create"); })
            .Returns(Task.CompletedTask);
        context.Photos.Setup(x => x.SaveAsync(It.IsAny<Guid>(), photo, It.IsAny<CancellationToken>()))
            .Callback<Guid, IFormFile, CancellationToken>((id, file, token) => { Assert.Equal(saved!.Id, id); steps.Add("store"); })
            .ReturnsAsync(url);
        context.LostItems.Setup(x => x.AddPhotoAsync(It.IsAny<Guid>(), url, It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((id, photoUrl, token) => { Assert.Equal(saved!.Id, id); steps.Add("link"); })
            .ReturnsAsync(() => new LostItemPhoto { Id = Guid.NewGuid(), LostItemId = saved!.Id, Url = url });
        context.Publisher.Setup(x => x.PublishAsync("items.lost_item.created", It.IsAny<LostItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, LostItemCreatedEvent, CancellationToken>((topic, evt, token) => { published = evt; steps.Add("publish"); })
            .Returns(ValueTask.CompletedTask);

        var result = await context.LostController(Guid.NewGuid()).ReportLostItem(request, CancellationToken.None);

        var dto = Assert.IsType<LostItemResponseDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(new[] { "create", "store", "link", "publish" }, steps);
        Assert.Single(saved!.Photos);
        Assert.Equal(new[] { url }, dto.PhotoUrls);
        Assert.Equal(url, dto.Photo!.Url);
        Assert.Equal(new[] { url }, published!.PhotoUrls);
        context.LostItems.VerifyAll();
        context.Photos.VerifyAll();
        context.Publisher.VerifyAll();
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("Category")]
    [InlineData("Description")]
    [InlineData("DateLost")]
    [InlineData("LastKnownLocation")]
    [InlineData("HiddenInformation")]
    public async Task MissingRequiredField_ReturnsValidationErrorWithoutWriting(string field)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.LostRequest();
        SetField(request, field, " ");

        var result = await context.LostController(Guid.NewGuid()).ReportLostItem(request, CancellationToken.None);

        Assert.Contains(field, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.LostItems.VerifyNoOtherCalls();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Category", "Invalid category")]
    [InlineData("DateLost", "14/10/2026")]
    public async Task InvalidCategoryOrDate_ReturnsValidationError(string field, string value)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.LostRequest();
        SetField(request, field, value);

        var result = await context.LostController(Guid.NewGuid()).ReportLostItem(request, CancellationToken.None);

        Assert.Contains(field, ItemControllerTestContext.Problem(result).Errors.Keys);
        context.LostItems.VerifyNoOtherCalls();
        context.Publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FutureDate_IsRejectedWithoutPersistence()
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.LostRequest();
        request.DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");
        var result = await context.LostController(Guid.NewGuid()).ReportLostItem(request, CancellationToken.None);
        Assert.Contains("DateLost", ItemControllerTestContext.Problem(result).Errors.Keys);
        context.LostItems.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Title", 151)]
    [InlineData("Category", 51)]
    [InlineData("Description", 2001)]
    [InlineData("LastKnownLocation", 256)]
    [InlineData("HiddenInformation", 501)]
    public void LengthLimits_AreDeclaredOnTheRequestDto(string field, int length)
    {
        var request = ItemControllerTestContext.LostRequest();
        SetField(request, field, new string('x', length));
        var errors = ItemControllerTestContext.ValidationErrors(request);
        Assert.Contains(errors, e => e.MemberNames.Contains(field));
    }

    [Theory]
    [InlineData("too-many")]
    [InlineData("spoofed")]
    [InlineData("unsupported")]
    public async Task InvalidPhotos_AreRejectedBeforePersistence(string kind)
    {
        var context = new ItemControllerTestContext();
        var request = ItemControllerTestContext.LostRequest();
        request.Photos = kind switch
        {
            "too-many" => [ItemControllerTestContext.JpegPhoto(), ItemControllerTestContext.JpegPhoto()],
            "spoofed" => [ItemControllerTestContext.Photo([0x01, 0x02, 0x03, 0x04], "image/jpeg")],
            _ => [ItemControllerTestContext.Photo([0x01], "application/pdf")]
        };
        var result = await context.LostController(Guid.NewGuid()).ReportLostItem(request, CancellationToken.None);
        Assert.Contains("Photos", ItemControllerTestContext.Problem(result).Errors.Keys);
        context.LostItems.VerifyNoOtherCalls();
        context.Photos.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingUserClaim_CannotCreateReport()
    {
        var context = new ItemControllerTestContext();
        var result = await context.LostController(null).ReportLostItem(ItemControllerTestContext.LostRequest(), CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        context.LostItems.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicRead_DoesNotExposeStoredHiddenInformation()
    {
        var context = new ItemControllerTestContext();
        var item = new LostItem
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Bag", Category = "Bags",
            Description = "Found later", DateLost = DateOnly.FromDateTime(DateTime.UtcNow),
            LastKnownLocation = "Library", HiddenInformation = "Never show this secret"
        };
        context.LostItems.Setup(x => x.GetByIdAsync(item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        var result = await context.LostController(Guid.NewGuid()).GetById(item.Id, CancellationToken.None);

        var dto = Assert.IsType<LostItemResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.DoesNotContain(item.HiddenInformation, JsonSerializer.Serialize(dto));
        context.LostItems.VerifyAll();
    }

    private static void SetField(ReportLostItemRequest request, string field, string value) =>
        typeof(ReportLostItemRequest).GetProperty(field)!.SetValue(request, value);
}
