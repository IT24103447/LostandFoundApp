using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Globalization;
using ItemService.Configuration;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Models.Events;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ItemService.Controllers;

[ApiController]
[Route("api/items/lost")]
[Authorize]
public class LostItemsController : ControllerBase
{
    private readonly ILostItemsRepository _items;
    private readonly IPhotoStorageService _photoStorage;
    private readonly IEventPublisher _publisher;
    private readonly ItemSettings _itemSettings;
    private readonly KafkaSettings _kafka;
    private readonly ILogger<LostItemsController> _logger;

    public LostItemsController(
        ILostItemsRepository items,
        IPhotoStorageService photoStorage,
        IEventPublisher publisher,
        IOptions<ItemSettings> itemSettings,
        IOptions<KafkaSettings> kafka,
        ILogger<LostItemsController> logger)
    {
        _items = items;
        _photoStorage = photoStorage;
        _publisher = publisher;
        _itemSettings = itemSettings.Value;
        _kafka = kafka.Value;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<LostItemResponseDto>> ReportLostItem(
        [FromForm] ReportLostItemRequest req,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(req.Title))
            errors["Title"] = ["Title is required."];
        if (string.IsNullOrWhiteSpace(req.Category))
        {
            errors["Category"] = ["Category is required."];
        }
        else if (!ItemCategories.IsValid(req.Category))
        {
            errors["Category"] = [$"Category must be one of: {string.Join(", ", ItemCategories.All)}."];
        }
        if (string.IsNullOrWhiteSpace(req.Description))
            errors["Description"] = ["Description is required."];
        if (string.IsNullOrWhiteSpace(req.LastKnownLocation))
            errors["LastKnownLocation"] = ["Last known location is required."];
        if (string.IsNullOrWhiteSpace(req.HiddenInformation))
            errors["HiddenInformation"] = ["Hidden information is required."];

        if (!DateOnly.TryParseExact(req.DateLost, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateLost))
        {
            errors["DateLost"] = ["Date lost must be a valid date in yyyy-MM-dd format."];
        }
        else if (dateLost > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            errors["DateLost"] = ["Date lost cannot be in the future."];
        }

        var photos = req.Photos ?? [];
        if (photos.Count > _itemSettings.MaxPhotosPerItem)
        {
            errors["Photos"] = [$"You can attach at most {_itemSettings.MaxPhotosPerItem} photos."];
        }
        else
        {
            for (var i = 0; i < photos.Count; i++)
            {
                var photo = photos[i];
                if (photo.Length == 0)
                {
                    errors["Photos"] = ["One or more photo files is empty."];
                    break;
                }
                if (photo.Length > _itemSettings.MaxPhotoSizeBytes)
                {
                    errors["Photos"] = [$"Each photo must be at most {_itemSettings.MaxPhotoSizeBytes / (1024 * 1024)} MB."];
                    break;
                }
                if (!_itemSettings.AllowedPhotoContentTypes.Contains(photo.ContentType, StringComparer.OrdinalIgnoreCase))
                {
                    errors["Photos"] = ["Photos must be JPEG, PNG, or WEBP images."];
                    break;
                }
                if (!await ImageSignatureValidator.MatchesDeclaredTypeAsync(photo, ct))
                {
                    errors["Photos"] = ["One or more files do not match a supported image format."];
                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var item = new LostItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = req.Title.Trim(),
            Category = req.Category.Trim(),
            Description = req.Description.Trim(),
            DateLost = dateLost,
            LastKnownLocation = req.LastKnownLocation.Trim(),
            HiddenInformation = req.HiddenInformation.Trim(),
            Status = LostItemStatus.ACTIVE,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _items.CreateAsync(item, ct);

        var photoUrls = new List<string>();
        foreach (var photo in photos)
        {
            var url = await _photoStorage.SaveAsync(item.Id, photo, ct);
            photoUrls.Add(url);
        }
        if (photoUrls.Count > 0)
        {
            await _items.AddPhotosAsync(item.Id, photoUrls, ct);
        }

        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.lost_item.created", new LostItemCreatedEvent
        {
            UserId = userId,
            LostItemId = item.Id,
            Title = item.Title,
            Category = item.Category,
            Description = item.Description,
            DateLost = item.DateLost,
            LastKnownLocation = item.LastKnownLocation,
            HiddenInformation = item.HiddenInformation,
            Status = item.Status.ToString(),
            PhotoUrls = photoUrls,
            CreatedAt = item.CreatedAt
        }, ct);

        _logger.LogInformation("User {UserId} reported lost item {LostItemId}.", userId, item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToDto(item, photoUrls));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LostItemResponseDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        return Ok(ToDto(item, item.Photos.Select(p => p.Url)));
    }

    private static LostItemResponseDto ToDto(LostItem item, IEnumerable<string> photoUrls) => new()
    {
        Id = item.Id,
        UserId = item.UserId,
        Title = item.Title,
        Category = item.Category,
        Description = item.Description,
        DateLost = item.DateLost.ToString("yyyy-MM-dd"),
        LastKnownLocation = item.LastKnownLocation,
        Status = item.Status.ToString(),
        PhotoUrls = photoUrls.ToList(),
        CreatedAt = item.CreatedAt
    };
}