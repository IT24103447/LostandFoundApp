using ItemService.Models;

namespace ItemService.Repositories;

public interface IAdminItemsRepository
{
    Task<AdminItemRecord?> GetByIdAsync(AdminItemType type, Guid id, CancellationToken ct = default);
    Task<bool> SoftDeleteActiveAsync(AdminItemType type, Guid id, DateTime deletedAt, CancellationToken ct = default);
}
