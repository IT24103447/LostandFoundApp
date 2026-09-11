using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.Repositories;
using ItemService.Services;

public class LostItemsControllerTests
{
    private readonly Mock<ILostItemsRepository> _repo = new();
    private readonly Mock<IPhotoStorageService> _photoStorage = new();
    private readonly Mock<IEventPublisher> _publisher = new();
    private readonly Mock<ILogger<LostItemsController>> _logger = new();

    private LostItemsController BuildController(
        int maxPhotos = 1,
        long maxPhotoSize = 5 * 1024 * 1024,
        Guid? userId = null)
    {
        var itemSettings = Options.Create(new ItemSettings
        {
            MaxPhotosPerItem = maxPhotos,
            MaxPhotoSizeBytes = maxPhotoSize,
            AllowedPhotoContentTypes = new[] { "image/jpeg", "image/png", "image/webp" }
        });
        var kafkaSettings = Options.Create(new KafkaSettings { TopicPrefix = "items" });

        var controller = new LostItemsController(
            _repo.Object, _photoStorage.Object, _publisher.Object,
            itemSettings, kafkaSettings, _logger.Object);

        var uid = userId ?? Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, uid.ToString())
        }, "TestAuth"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    private static ReportLostItemRequest ValidRequest() => new()
    {
        Title = "Black leather wallet",
        Category = "Accessories",
        Description = "Bifold wallet, slightly worn.",
        DateLost = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        LastKnownLocation = "Colombo City Centre",
        HiddenInformation = "Torn inner pocket with a bus ticket stub."
    };

    // UNIT-01
    [Fact]
    public async Task ReportLostItem_ValidRequest_ReturnsCreatedWithActiveStatus()
    {
        var controller = BuildController();
        var req = ValidRequest();

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<LostItemResponseDto>(created.Value);
        Assert.Equal("ACTIVE", dto.Status);
        _repo.Verify(r => r.CreateAsync(It.Is<LostItem>(i => i.Status == LostItemStatus.ACTIVE), It.IsAny<CancellationToken>()), Times.Once);
    }

    // UNIT-02 / API-20 (hidden info never exposed via DTO)
    [Fact]
    public async Task ReportLostItem_ResponseDto_NeverContainsHiddenInformation()
    {
        var controller = BuildController();
        var req = ValidRequest();

        var result = await controller.ReportLostItem(req, CancellationToken.None);
        var created = (CreatedAtActionResult)result.Result!;
        var dto = (LostItemResponseDto)created.Value!;

        var dtoJson = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(req.HiddenInformation, dtoJson);

        var props = typeof(LostItemResponseDto).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(props, n => n.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    // UNIT-09 (fixed: BadRequestObjectResult)
    [Fact]
    public async Task ReportLostItem_FutureDateLost_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.DateLost = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("DateLost"));
        _repo.Verify(r => r.CreateAsync(It.IsAny<LostItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // UNIT-10 (boundary — today is allowed)
    [Fact]
    public async Task ReportLostItem_DateLostIsToday_IsAccepted()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.DateLost = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // UNIT-08 (fixed: BadRequestObjectResult)
    [Fact]
    public async Task ReportLostItem_MalformedDateLost_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.DateLost = "28/08/2026"; // wrong format, controller expects yyyy-MM-dd

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("DateLost"));
    }

    // UNIT-15 (fixed: BadRequestObjectResult)
    [Fact]
    public async Task ReportLostItem_TooManyPhotos_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Photos = new List<IFormFile>
        {
            MockRealJpeg("a.jpg"),
            MockRealJpeg("b.jpg")
        };

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("Photos"));
    }

    // UNIT-16
    [Fact]
    public async Task ReportLostItem_PhotoExceedsMaxSize_ReturnsValidationProblem()
    {
        var controller = BuildController(maxPhotoSize: 1024);
        var req = ValidRequest();
        req.Photos = new List<IFormFile> { MockPhoto("big.jpg", "image/jpeg", 2048) };

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("Photos"));
    }

    // UNIT-17 (fixed: BadRequestObjectResult)
    [Fact]
    public async Task ReportLostItem_DisallowedContentType_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Photos = new List<IFormFile> { MockPhoto("doc.pdf", "application/pdf", 1024) };

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("Photos"));
    }

    // UNIT-18 (fixed: BadRequestObjectResult)
    [Fact]
    public async Task ReportLostItem_ZeroLengthPhoto_ReturnsValidationProblem()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Photos = new List<IFormFile> { MockPhoto("empty.jpg", "image/jpeg", 0) };

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.True(problem.Errors.ContainsKey("Photos"));
    }

    // API-08 / UNIT-05 — photos truly optional
    [Fact]
    public async Task ReportLostItem_NoPhotos_StillSucceeds_AndSkipsAddPhotoCall()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Photos = null;

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        _repo.Verify(r => r.AddPhotoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // UNIT-13 / API-22 — Kafka event carries hidden information
    [Fact]
    public async Task ReportLostItem_PublishesEvent_ContainingHiddenInformation()
    {
        var controller = BuildController();
        var req = ValidRequest();

        LostItemCreatedEvent? captured = null;
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<LostItemCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, LostItemCreatedEvent, CancellationToken>((_, evt, _) => captured = evt)
            .Returns(ValueTask.CompletedTask);

        await controller.ReportLostItem(req, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("lost_item.created", captured!.EventType);
        Assert.Equal(req.HiddenInformation, captured.HiddenInformation);
    }

    // UNIT-20
    [Fact]
    public async Task GetById_ItemDoesNotExist_ReturnsNotFound()
    {
        var controller = BuildController();
        _repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((LostItem?)null);

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ReportLostItem_WhitespaceOnlyTitle_IsRejected()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Title = "   ";

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains("Title", problem.Errors.Keys);
    }

    [Fact]
    public async Task ReportLostItem_UnsupportedCategory_IsRejected()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Category = "not-a-supported-category";

        var result = await controller.ReportLostItem(req, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
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
