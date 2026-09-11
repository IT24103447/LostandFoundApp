using System.Globalization;
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

    public ItemsController(IItemsSearchRepository search)
    {
        _search = search;
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
}