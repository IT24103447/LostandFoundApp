using ItemService.Models;

namespace ItemService.Repositories;

public interface ILostItemsRepository
{
    Task CreateAsync(LostItem item, CancellationToken ct = default);
    Task<LostItemPhoto> AddPhotoAsync(Guid lostItemId, string photoUrl, CancellationToken ct = default);
    Task<LostItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<LostItem>> GetByUserIdAsync(Guid userId, CancellationToken ct = default); // AC 1 + AC 2
    Task UpdateAsync(LostItem item, CancellationToken ct = default);
    Task DeletePhotosAsync(Guid lostItemId, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid id, LostItemStatus status, DateTime updatedAt, CancellationToken ct = default);
}
