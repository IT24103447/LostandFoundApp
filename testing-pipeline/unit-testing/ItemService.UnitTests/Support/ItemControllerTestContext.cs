using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ItemService.UnitTests.Support;

internal sealed class ItemControllerTestContext
{
    public Mock<ILostItemsRepository> LostItems { get; } = new(MockBehavior.Strict);
    public Mock<IFoundItemsRepository> FoundItems { get; } = new(MockBehavior.Strict);
    public Mock<IItemsSearchRepository> SearchItems { get; } = new(MockBehavior.Strict);
    public Mock<IPhotoStorageService> Photos { get; } = new(MockBehavior.Strict);
    public Mock<IEventPublisher> Publisher { get; } = new(MockBehavior.Strict);

    private static readonly IOptions<ItemSettings> ItemsOptions = Options.Create(new ItemSettings());
    private static readonly IOptions<KafkaSettings> KafkaOptions = Options.Create(new KafkaSettings { TopicPrefix = "items" });

    public LostItemsController LostController(Guid? userId) => new(
        LostItems.Object, Photos.Object, Publisher.Object, ItemsOptions, KafkaOptions,
        NullLogger<LostItemsController>.Instance)
    {
        ControllerContext = ContextFor(userId)
    };

    public FoundItemsController FoundController(Guid? userId) => new(
        FoundItems.Object, Photos.Object, Publisher.Object, ItemsOptions, KafkaOptions,
        NullLogger<FoundItemsController>.Instance)
    {
        ControllerContext = ContextFor(userId)
    };

    public ItemsController ItemsController(Guid? userId) => new(
        SearchItems.Object, LostItems.Object, FoundItems.Object)
    {
        ControllerContext = ContextFor(userId)
    };

    private static ControllerContext ContextFor(Guid? userId)
    {
        var claims = userId is Guid id
            ? new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }
            : Array.Empty<Claim>();
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "UnitTest"))
            }
        };
    }

    public static ReportLostItemRequest LostRequest() => new()
    {
        Title = "Black backpack", Category = "Bags", Description = "Black canvas with a front pocket",
        DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd"),
        LastKnownLocation = "Central library", HiddenInformation = "Blue initials inside the lining"
    };

    public static ReportFoundItemRequest FoundRequest() => new()
    {
        Title = "Silver keys", Category = "Keys", Description = "Three keys on a ring",
        DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd"),
        LocationFound = "Bus stop", HiddenInformation = "Tiny green tag on the ring"
    };

    public static UpdateLostItemRequest LostUpdate() => new()
    {
        Title = "Updated backpack", Category = "Bags", Description = "Updated description",
        DateLost = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd"),
        LastKnownLocation = "West entrance", HiddenInformation = "New private detail"
    };

    public static UpdateFoundItemRequest FoundUpdate() => new()
    {
        Title = "Updated keys", Category = "Keys", Description = "Updated found description",
        DateFound = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd"),
        LocationFound = "East entrance", HiddenInformation = "New found private detail"
    };

    public static IFormFile JpegPhoto() => Photo([0xFF, 0xD8, 0xFF, 0x00], "image/jpeg");

    public static IFormFile Photo(byte[] bytes, string contentType)
    {
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Photos", "item.jpg")
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };
        return file;
    }

    public static IReadOnlyList<ValidationResult> ValidationErrors(object request)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true);
        return errors;
    }

    public static ValidationProblemDetails Problem<T>(ActionResult<T> result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
}
