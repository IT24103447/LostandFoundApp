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
using Xunit;

public class FoundItemsControllerTests
{
    private readonly Mock<IFoundItemsRepository> _repo = new();
    private readonly Mock<IPhotoStorageService> _photoStorage = new();
    private readonly Mock<IEventPublisher> _publisher = new();
    private readonly Mock<ILogger<FoundItemsController>> _logger = new();

    private FoundItemsController BuildController(
        int maxPhotos = 1,
        long maxPhotoSize = 5 * 1024 * 1024,
        Guid? userId = null)
    {
        var itemSettings = Options.Create(new ItemSettings
        {
            MaxPhotosPerItem = maxPhotos,
            MaxPhotoSizeBytes = maxPhotoSize,
            AllowedPhotoContentTypes = ["image/jpeg", "image/png", "image/webp"]
        });
        var kafkaSettings = Options.Create(new KafkaSettings { TopicPrefix = "items" });
        var controller = new FoundItemsController(
            _repo.Object, _photoStorage.Object, _publisher.Object,
            itemSettings, kafkaSettings, _logger.Object);

        var uid = userId ?? Guid.NewGuid();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, uid.ToString())
                ], "TestAuth"))
            }
        };
        return controller;
    }

    private static ReportFoundItemRequest ValidRequest() => new()
    {
        Title = "Black wallet",
        Category = "Accessories",
        Description = "Found near the library entrance.",
        DateFound = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        LocationFound = "Main library entrance",
        HiddenInformation = "Small scratch beside the clasp"
    };

    [Fact]
    public async Task ReportFoundItem_ValidRequest_CreatesActiveItemForClaimUser()
    {
        var userId = Guid.NewGuid();
        var controller = BuildController(userId: userId);
        var request = ValidRequest();
        FoundItem? created = null;
        _repo.Setup(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()))
            .Callback<FoundItem, CancellationToken>((item, _) => created = item)
            .Returns(Task.CompletedTask);
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var response = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<FoundItemResponseDto>(response.Value);
        Assert.Equal("ACTIVE", dto.Status);
        Assert.NotNull(created);
        Assert.Equal(userId, created!.UserId);
        Assert.Equal(FoundItemStatus.ACTIVE, created.Status);
        Assert.Equal(DateOnly.Parse(request.DateFound), created.DateFound);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.NotEqual(default, created.UpdatedAt);
    }

    [Fact]
    public async Task ReportFoundItem_ResponseNeverContainsHiddenInformation()
    {
        var controller = BuildController();
        var request = ValidRequest();
        var result = await controller.ReportFoundItem(request, CancellationToken.None);
        var response = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<FoundItemResponseDto>(response.Value);
        var json = System.Text.Json.JsonSerializer.Serialize(dto);

        Assert.DoesNotContain(request.HiddenInformation, json, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenInformation", typeof(FoundItemResponseDto).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task ReportFoundItem_PublishesCompleteEventIncludingHiddenInformation()
    {
        var controller = BuildController();
        var request = ValidRequest();
        FoundItemCreatedEvent? published = null;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, FoundItemCreatedEvent, CancellationToken>((_, evt, _) => published = evt)
            .Returns(ValueTask.CompletedTask);

        await controller.ReportFoundItem(request, CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal("found_item.created", published!.EventType);
        Assert.Equal(request.HiddenInformation, published.HiddenInformation);
        Assert.Equal(request.LocationFound, published.LocationFound);
        Assert.Equal("ACTIVE", published.Status);
    }

    [Fact]
    public async Task ReportFoundItem_FutureDate_ReturnsValidationProblemAndDoesNotWrite()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.DateFound = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("DateFound", problem.Errors.Keys);
        _repo.Verify(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportFoundItem_MalformedDate_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.DateFound = "09/08/2026";

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("DateFound", problem.Errors.Keys);
    }

    [Fact]
    public async Task ReportFoundItem_TooManyPhotos_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Photos = [MockRealJpeg("a.jpg"), MockRealJpeg("b.jpg")];

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Photos", problem.Errors.Keys);
    }

    [Theory]
    [InlineData("empty.jpg", "image/jpeg", 0)]
    [InlineData("large.jpg", "image/jpeg", 1025)]
    [InlineData("document.pdf", "application/pdf", 10)]
    public async Task ReportFoundItem_InvalidPhoto_ReturnsValidationProblem(string name, string contentType, long length)
    {
        var controller = BuildController(maxPhotoSize: 1024);
        var request = ValidRequest();
        request.Photos = [MockPhoto(name, contentType, length)];

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Photos", problem.Errors.Keys);
    }

    [Fact]
    public async Task GetById_MissingItem_ReturnsNotFound()
    {
        var controller = BuildController();
        _repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FoundItem?)null);

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ReportFoundItem_InvalidSession_ReturnsUnauthorized()
    {
        var controller = BuildController();
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await controller.ReportFoundItem(ValidRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _repo.Verify(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportFoundItem_SpoofedJpegContentType_IsRejected()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Photos = [MockPhoto("not-an-image.jpg", "image/jpeg", 4, validImageSignature: false)];

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Photos", problem.Errors.Keys);
        _photoStorage.Verify(s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportFoundItem_MultiplePhotosProvided_ExceedsMaxIsRejected()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Photos = [MockRealJpeg("found-1.jpg"), MockRealJpeg("found-2.jpg")];

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Photos", problem.Errors.Keys);
        _repo.Verify(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Never);
        _photoStorage.Verify(s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportFoundItem_PhotoStorageFailureOccursAfterDatabaseCreate()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Photos = [MockRealJpeg("found.jpg")];
        _photoStorage.Setup(s => s.SaveAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));

        await Assert.ThrowsAsync<IOException>(() => controller.ReportFoundItem(request, CancellationToken.None));
        _repo.Verify(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportFoundItem_EventFailureOccursAfterDatabaseCreate()
    {
        var controller = BuildController();
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<FoundItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("event publisher unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ReportFoundItem(ValidRequest(), CancellationToken.None));
        _repo.Verify(r => r.CreateAsync(It.IsAny<FoundItem>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportFoundItem_WhitespaceOnlyTitle_IsRejected()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Title = "   ";

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Title", problem.Errors.Keys);
    }

    [Fact]
    public async Task ReportFoundItem_UnsupportedCategory_IsRejected()
    {
        var controller = BuildController();
        var request = ValidRequest();
        request.Category = "not-a-supported-category";

        var result = await controller.ReportFoundItem(request, CancellationToken.None);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains("Category", problem.Errors.Keys);
    }

    private static IFormFile MockRealJpeg(string fileName, long length = 1024) =>
        MockPhoto(fileName, "image/jpeg", length, validImageSignature: true);

    private static IFormFile MockPhoto(string fileName, string contentType, long length, bool validImageSignature = false)
    {
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.FileName).Returns(fileName);
        mock.Setup(f => f.ContentType).Returns(contentType);
        mock.Setup(f => f.Length).Returns(length);
        var bytes = new byte[Math.Max(length, 1)];
        if (validImageSignature && length >= 3 && contentType == "image/jpeg")
        {
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
        }
        mock.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(bytes));
        return mock.Object;
    }
}
