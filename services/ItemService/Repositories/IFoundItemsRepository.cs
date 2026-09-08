using ItemService.Models;

namespace ItemService.Repositories;

public interface IFoundItemsRepository
{
    Task CreateAsync(FoundItem item, CancellationToken ct = default);
    Task AddPhotosAsync(Guid foundItemId, IEnumerable<string> photoUrls, CancellationToken ct = default);
    Task<FoundItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
