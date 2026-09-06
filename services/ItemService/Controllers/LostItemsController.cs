using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Globalization;
using ItemService.Configuration;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
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
    private readonly ItemSettings _itemSettings;
    private readonly ILogger<LostItemsController> _logger;

    public LostItemsController(
        ILostItemsRepository items,
        IOptions<ItemSettings> itemSettings,
        ILogger<LostItemsController> logger)
    {
        _items = items;
        _itemSettings = itemSettings.Value;
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

        if (!DateOnly.TryParseExact(req.DateLost, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateLost))
        {
            errors["DateLost"] = ["Date lost must be a valid date in yyyy-MM-dd format."];
        }
        else if (dateLost > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            errors["DateLost"] = ["Date lost cannot be in the future."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var item = new LostItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = req.Title,
            Category = req.Category,
            Description = req.Description,
            DateLost = dateLost,
            LastKnownLocation = req.LastKnownLocation,
            HiddenInformation = req.HiddenInformation,
            Status = LostItemStatus.ACTIVE,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _items.CreateAsync(item, ct);

        _logger.LogInformation("User {UserId} reported lost item {LostItemId}.", userId, item.Id);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToDto(item, []));
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