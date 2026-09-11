using ItemService.Models;
using ItemService.Models.Dtos;

namespace ItemService.Repositories;

public interface IItemsSearchRepository
{
    Task<PagedResultDto<ItemSummaryDto>> SearchAsync(ItemSearchQuery query, CancellationToken ct = default);
}