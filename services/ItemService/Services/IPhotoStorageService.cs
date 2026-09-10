using Microsoft.AspNetCore.Http;

namespace ItemService.Services;

public interface IPhotoStorageService
{
    Task<string> SaveAsync(Guid lostItemId, IFormFile photo, CancellationToken ct = default);

    // Deletes the underlying blob/file referenced by the stored photo URL.
    // Safe to call even if the underlying blob/file no longer exists.
    Task DeleteAsync(string photoUrl, CancellationToken ct = default);
}
