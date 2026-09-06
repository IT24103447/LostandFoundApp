using ItemService.Models;

namespace ItemService.Repositories;

public interface ILostItemsRepository
{
    Task CreateAsync(LostItem item, CancellationToken ct = default);
    Task AddPhotosAsync(Guid lostItemId, IEnumerable<string> photoUrls, CancellationToken ct = default);
    Task<LostItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
