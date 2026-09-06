using Microsoft.AspNetCore.Http;

namespace ItemService.Services;

public interface IPhotoStorageService
{
    Task<string> SaveAsync(Guid lostItemId, IFormFile photo, CancellationToken ct = default);
}
