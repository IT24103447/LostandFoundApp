using ItemService.Models;

namespace ItemService.Repositories;

public interface ILostItemsRepository
{
    Task CreateAsync(LostItem item, CancellationToken ct = default);
    Task<LostItemPhoto> AddPhotoAsync(Guid lostItemId, string photoUrl, CancellationToken ct = default);
    Task<LostItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(LostItem item, CancellationToken ct = default);
    Task DeletePhotosAsync(Guid lostItemId, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid id, LostItemStatus status, DateTime updatedAt, CancellationToken ct = default);
}
