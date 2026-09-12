using ItemService.Models;

namespace ItemService.Repositories;

public interface IFoundItemsRepository
{
    Task CreateAsync(FoundItem item, CancellationToken ct = default);
    Task<FoundItemPhoto> AddPhotoAsync(Guid foundItemId, string photoUrl, CancellationToken ct = default);
    Task<FoundItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(FoundItem item, CancellationToken ct = default);
    Task DeletePhotosAsync(Guid foundItemId, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid id, FoundItemStatus status, DateTime updatedAt, CancellationToken ct = default);
}
