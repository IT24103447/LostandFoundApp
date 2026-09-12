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
using Microsoft.AspNetCore.Http;
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

        IFormFile? photo = req.Photos?.Count > 0 ? req.Photos[0] : null;
        if (req.Photos?.Count > _itemSettings.MaxPhotosPerItem)
        {
            errors["Photos"] = [$"You can attach at most {_itemSettings.MaxPhotosPerItem} photo."];
        }
        else if (photo is not null)
        {
            if (photo.Length == 0)
            {
                errors["Photos"] = ["The photo file is empty."];
            }
            else if (photo.Length > _itemSettings.MaxPhotoSizeBytes)
            {
                errors["Photos"] = [$"The photo must be at most {_itemSettings.MaxPhotoSizeBytes / (1024 * 1024)} MB."];
            }
            else if (!_itemSettings.AllowedPhotoContentTypes.Contains(photo.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                errors["Photos"] = ["The photo must be a JPEG, PNG, or WEBP image."];
            }
            else if (!await ImageSignatureValidator.MatchesDeclaredTypeAsync(photo, ct))
            {
                errors["Photos"] = ["The photo file does not match a supported image format."];
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

        if (photo is not null)
        {
            var url = await _photoStorage.SaveAsync(item.Id, photo, ct);
            var savedPhoto = await _items.AddPhotoAsync(item.Id, url, ct);
            item.Photos.Add(savedPhoto);
        }

        var photoUrls = item.Photos.Select(p => p.Url).ToList();

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

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToDto(item));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LostItemResponseDto>> GetById(Guid id, CancellationToken ct)
    {
        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        return Ok(ToDto(item));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<LostItemResponseDto>> UpdateLostItem(
        Guid id,
        [FromBody] UpdateLostItemRequest req,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        // Scenario 2 - Unauthorized Edit Attempt: only the original reporter may edit.
        if (item.UserId != userId)
        {
            return StatusCode(403, new { error = "You are not allowed to edit this report." });
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

        // Scenario 3 - Invalid Update Submitted.
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        item.Title = req.Title.Trim();
        item.Category = req.Category.Trim();
        item.Description = req.Description.Trim();
        item.DateLost = dateLost;
        item.LastKnownLocation = req.LastKnownLocation.Trim();
        item.HiddenInformation = req.HiddenInformation.Trim();
        item.UpdatedAt = DateTime.UtcNow;

        await _items.UpdateAsync(item, ct);

        await PublishUpdatedEvent(item, userId, ct);

        _logger.LogInformation("User {UserId} updated lost item {LostItemId}.", userId, item.Id);

        return Ok(ToDto(item));
    }

    [HttpPut("{id:guid}/photo")]
    public async Task<ActionResult<LostItemResponseDto>> ReplaceLostItemPhoto(
        Guid id,
        IFormFile photo,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        if (item.UserId != userId)
        {
            return StatusCode(403, new { error = "You are not allowed to edit this report." });
        }

        var errors = new Dictionary<string, string[]>();
        if (photo is null || photo.Length == 0)
        {
            errors["Photo"] = ["A photo file is required."];
        }
        else if (photo.Length > _itemSettings.MaxPhotoSizeBytes)
        {
            errors["Photo"] = [$"The photo must be at most {_itemSettings.MaxPhotoSizeBytes / (1024 * 1024)} MB."];
        }
        else if (!_itemSettings.AllowedPhotoContentTypes.Contains(photo.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            errors["Photo"] = ["The photo must be a JPEG, PNG, or WEBP image."];
        }
        else if (!await ImageSignatureValidator.MatchesDeclaredTypeAsync(photo, ct))
        {
            errors["Photo"] = ["The photo file does not match a supported image format."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        // Remove any existing photo (blob/file + row) before saving the new one —
        // MaxPhotosPerItem = 1 means this is always a full replace, never an add.
        foreach (var existing in item.Photos)
        {
            await _photoStorage.DeleteAsync(existing.Url, ct);
        }
        await _items.DeletePhotosAsync(item.Id, ct);

        var url = await _photoStorage.SaveAsync(item.Id, photo!, ct);
        var savedPhoto = await _items.AddPhotoAsync(item.Id, url, ct);

        item.Photos.Clear();
        item.Photos.Add(savedPhoto);
        item.UpdatedAt = DateTime.UtcNow;
        await _items.UpdateAsync(item, ct);

        await PublishUpdatedEvent(item, userId, ct);

        _logger.LogInformation("User {UserId} replaced the photo on lost item {LostItemId}.", userId, item.Id);

        return Ok(ToDto(item));
    }

    [HttpDelete("{id:guid}/photo")]
    public async Task<ActionResult<LostItemResponseDto>> DeleteLostItemPhoto(Guid id, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        if (item.UserId != userId)
        {
            return StatusCode(403, new { error = "You are not allowed to edit this report." });
        }

        foreach (var existing in item.Photos)
        {
            await _photoStorage.DeleteAsync(existing.Url, ct);
        }
        await _items.DeletePhotosAsync(item.Id, ct);
        item.Photos.Clear();

        item.UpdatedAt = DateTime.UtcNow;
        await _items.UpdateAsync(item, ct);

        await PublishUpdatedEvent(item, userId, ct);

        _logger.LogInformation("User {UserId} removed the photo from lost item {LostItemId}.", userId, item.Id);

        return Ok(ToDto(item));
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<LostItemResponseDto>> ResolveLostItem(Guid id, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var item = await _items.GetByIdAsync(id, ct);
        if (item is null)
        {
            return NotFound(new { error = "Lost item not found." });
        }

        // Scenario 2 - Unauthorized Resolution Attempt: only the original reporter may resolve.
        if (item.UserId != userId)
        {
            return StatusCode(403, new { error = "You are not allowed to resolve this report." });
        }

        if (item.Status != LostItemStatus.ACTIVE)
        {
            return Conflict(new { error = "Only active items can be marked as resolved." });
        }

        item.Status = LostItemStatus.RESOLVED;
        item.UpdatedAt = DateTime.UtcNow;

        await _items.UpdateStatusAsync(item.Id, item.Status, item.UpdatedAt, ct);

        // Scenario 1 - publish the FULL current item state, including HiddenInformation,
        // so the Matching Service can remove or finalize any related match records.
        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.lost_item.resolved", new LostItemResolvedEvent
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
            PhotoUrls = item.Photos.Select(p => p.Url).ToList(),
            ResolvedAt = item.UpdatedAt
        }, ct);

        _logger.LogInformation("User {UserId} marked lost item {LostItemId} as resolved.", userId, item.Id);

        return Ok(ToDto(item));
    }

    private async Task PublishUpdatedEvent(LostItem item, Guid userId, CancellationToken ct)
    {
        // Scenario 1 - publish the FULL current item state, including HiddenInformation,
        // so the Matching Service can re-evaluate matches against the correction.
        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.lost_item.updated", new LostItemUpdatedEvent
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
            PhotoUrls = item.Photos.Select(p => p.Url).ToList(),
            UpdatedAt = item.UpdatedAt
        }, ct);
    }

    private static LostItemResponseDto ToDto(LostItem item)
    {
        var photo = item.Photos.FirstOrDefault();
        return new LostItemResponseDto
        {
            Id = item.Id,
            UserId = item.UserId,
            Title = item.Title,
            Category = item.Category,
            Description = item.Description,
            DateLost = item.DateLost.ToString("yyyy-MM-dd"),
            LastKnownLocation = item.LastKnownLocation,
            Status = item.Status.ToString(),
            PhotoUrls = item.Photos.Select(p => p.Url).ToList(),
            Photo = photo is null ? null : new ItemPhotoDto { Id = photo.Id, Url = photo.Url },
            CreatedAt = item.CreatedAt
        };
    }
}