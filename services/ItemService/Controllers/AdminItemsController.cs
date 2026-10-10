using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ItemService.Authorization;
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
[Route("api/admin/items")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class AdminItemsController : ControllerBase
{
    private const string ItemRoute = "{type:regex(^(lost|found)$)}/{id:guid}";
    private const string ActiveStatus = "ACTIVE";
    private const string ItemNotFoundMessage = "Item not found.";

    private readonly IAdminItemsRepository _items;
    private readonly IEventPublisher _publisher;
    private readonly KafkaSettings _kafka;
    private readonly ILogger<AdminItemsController> _logger;

    public AdminItemsController(
        IAdminItemsRepository items,
        IEventPublisher publisher,
        IOptions<KafkaSettings> kafka,
        ILogger<AdminItemsController> logger)
    {
        _items = items;
        _publisher = publisher;
        _kafka = kafka.Value;
        _logger = logger;
    }

    [HttpGet(ItemRoute)]
    public async Task<ActionResult<AdminItemDto>> GetItem(string type, Guid id, CancellationToken ct)
    {
        var item = await _items.GetByIdAsync(ParseType(type), id, ct);
        if (item is null)
        {
            return NotFound(new { error = ItemNotFoundMessage });
        }

        return Ok(ToDto(item));
    }

    [HttpGet(ItemRoute + "/photos")]
    public async Task<ActionResult<AdminItemPhotosDto>> GetItemPhotos(string type, Guid id, CancellationToken ct)
    {
        var itemType = ParseType(type);
        if (await _items.GetByIdAsync(itemType, id, ct) is null)
        {
            return NotFound(new { error = ItemNotFoundMessage });
        }

        return Ok(new AdminItemPhotosDto { PhotoUrls = await _items.GetPhotoUrlsAsync(itemType, id, ct) });
    }

    [HttpDelete(ItemRoute)]
    public async Task<IActionResult> DeleteItem(string type, Guid id, CancellationToken ct)
    {
        var itemType = ParseType(type);
        var item = await _items.GetByIdAsync(itemType, id, ct);
        if (item is null || item.IsDeleted)
        {
            return NotFound(new { error = ItemNotFoundMessage });
        }

        if (item.Status != ActiveStatus)
        {
            return Conflict(new { error = $"Only active listings can be deleted. This listing is {item.Status}." });
        }

        if (!await _items.SoftDeleteActiveAsync(itemType, id, DateTime.UtcNow, ct))
        {
            return Conflict(new { error = "The listing changed while it was being deleted. Reload it and try again." });
        }

        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.item.delete_requested", new ItemDeleteRequestedEvent
        {
            UserId = item.UserId,
            ItemId = item.Id,
            ItemType = itemType.ToString()
        }, ct);

        _logger.LogInformation(
            "Admin {AdminId} deleted {ItemType} item {ItemId} owned by user {UserId}.",
            GetAdminId(), itemType, item.Id, item.UserId);

        return Ok(new { message = "Item deleted." });
    }

    private static AdminItemType ParseType(string type) => Enum.Parse<AdminItemType>(type, ignoreCase: true);

    private string? GetAdminId() =>
        User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static AdminItemDto ToDto(AdminItemRecord item) => new()
    {
        Id = item.Id,
        Type = item.Type.ToString(),
        UserId = item.UserId,
        Title = item.Title,
        Description = item.Description,
        Status = item.Status,
        CreatedAt = item.CreatedAt,
        DeletedAt = item.DeletedAt
    };
}
