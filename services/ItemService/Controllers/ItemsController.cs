using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ItemService.Models;
using ItemService.Models.Dtos;
using ItemService.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ItemService.Controllers;

[ApiController]
[Route("api/items")]
[Authorize]
public class ItemsController : ControllerBase
{
    private readonly IItemsSearchRepository _search;
    private readonly ILostItemsRepository _lostItems;
    private readonly IFoundItemsRepository _foundItems;

    public ItemsController(
        IItemsSearchRepository search,
        ILostItemsRepository lostItems,
        IFoundItemsRepository foundItems)
    {
        _search = search;
        _lostItems = lostItems;
        _foundItems = foundItems;
    }

    /// Browse/search active lost and found items, newest first, with optional
    /// keyword search, category filter, item type filter, and date-range filter.
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<ItemSummaryDto>>> Search(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] string? type,
        [FromQuery] string? dateFrom,
        [FromQuery] string? dateTo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();

        if (!string.IsNullOrWhiteSpace(category) && !ItemCategories.IsValid(category))
        {
            errors["Category"] = [$"Category must be one of: {string.Join(", ", ItemCategories.All)}."];
        }

        string? normalizedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            normalizedType = type.Trim().ToUpperInvariant();
            if (normalizedType is not ("LOST" or "FOUND"))
            {
                errors["Type"] = ["Type must be either 'LOST' or 'FOUND'."];
            }
        }

        DateOnly? parsedDateFrom = null;
        if (!string.IsNullOrWhiteSpace(dateFrom))
        {
            if (!DateOnly.TryParseExact(dateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                errors["DateFrom"] = ["DateFrom must be a valid date in yyyy-MM-dd format."];
            else
                parsedDateFrom = d;
        }

        DateOnly? parsedDateTo = null;
        if (!string.IsNullOrWhiteSpace(dateTo))
        {
            if (!DateOnly.TryParseExact(dateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                errors["DateTo"] = ["DateTo must be a valid date in yyyy-MM-dd format."];
            else
                parsedDateTo = d;
        }

        if (parsedDateFrom is not null && parsedDateTo is not null && parsedDateFrom > parsedDateTo)
        {
            errors["DateRange"] = ["DateFrom must not be after DateTo."];
        }

        if (page < 1)
        {
            errors["Page"] = ["Page must be 1 or greater."];
        }

        if (pageSize is < 1 or > 50)
        {
            errors["PageSize"] = ["PageSize must be between 1 and 50."];
        }

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var result = await _search.SearchAsync(new ItemSearchQuery
        {
            Keyword = q?.Trim(),
            Category = category?.Trim(),
            ItemType = normalizedType,
            DateFrom = parsedDateFrom,
            DateTo = parsedDateTo,
            Page = page,
            PageSize = pageSize
        }, ct);

        return Ok(result);
    }

    /// View Item Details story.
    /// Valid ID returns public item details without HiddenInformation.
    /// Unknown or soft-deleted item -> 404.
    /// Resolved item -> 404 for anyone except the original reporter.
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var lostItem = await _lostItems.GetByIdAsync(id, ct);
        if (lostItem is not null)
        {
            if (lostItem.Status == LostItemStatus.RESOLVED && lostItem.UserId != userId)
            {
                return NotFound(new { error = "Item not found." });
            }

            return Ok(ToDetailDto(lostItem));
        }

        var foundItem = await _foundItems.GetByIdAsync(id, ct);
        if (foundItem is not null)
        {
            if (foundItem.Status == FoundItemStatus.RESOLVED && foundItem.UserId != userId)
            {
                return NotFound(new { error = "Item not found." });
            }

            return Ok(ToDetailDto(foundItem));
        }

        return NotFound(new { error = "Item not found." });
    }

    // HiddenInformation is intentionally never read off LostItem/FoundItem here -
    // this is what keeps it out of the public item-details response.
    private static ItemDetailDto ToDetailDto(LostItem item)
    {
        var photo = item.Photos.FirstOrDefault();
        return new ItemDetailDto
        {
            Id = item.Id,
            Type = "LOST",
            Title = item.Title,
            Description = item.Description,
            Category = item.Category,
            Location = item.LastKnownLocation,
            Date = item.DateLost.ToString("yyyy-MM-dd"),
            Status = item.Status.ToString(),
            PhotoUrls = item.Photos.Select(p => p.Url).ToList(),
            Photo = photo is null ? null : new ItemPhotoDto { Id = photo.Id, Url = photo.Url },
            CreatedAt = item.CreatedAt
        };
    }

    private static ItemDetailDto ToDetailDto(FoundItem item)
    {
        var photo = item.Photos.FirstOrDefault();
        return new ItemDetailDto
        {
            Id = item.Id,
            Type = "FOUND",
            Title = item.Title,
            Description = item.Description,
            Category = item.Category,
            Location = item.LocationFound,
            Date = item.DateFound.ToString("yyyy-MM-dd"),
            Status = item.Status.ToString(),
            PhotoUrls = item.Photos.Select(p => p.Url).ToList(),
            Photo = photo is null ? null : new ItemPhotoDto { Id = photo.Id, Url = photo.Url },
            CreatedAt = item.CreatedAt
        };
    }
}