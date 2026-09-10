using ItemService.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ItemService.Services;

public class LocalPhotoStorageService : IPhotoStorageService
{
    private readonly ItemSettings _settings;
    private readonly IWebHostEnvironment _env;

    public LocalPhotoStorageService(IOptions<ItemSettings> settings, IWebHostEnvironment env)
    {
        _settings = settings.Value;
        _env = env;
    }

    public async Task<string> SaveAsync(Guid lostItemId, IFormFile photo, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(photo.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = photo.ContentType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg"
            };
        }

        var fileName = $"{Guid.NewGuid()}{extension}";
        var relativeDir = Path.Combine(lostItemId.ToString());
        var absoluteDir = Path.Combine(_env.ContentRootPath, _settings.PhotoStoragePath, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        var absolutePath = Path.Combine(absoluteDir, fileName);
        await using (var stream = File.Create(absolutePath))
        {
            await photo.CopyToAsync(stream, ct);
        }

        var publicPath = $"{_settings.PhotoPublicPath}/{lostItemId}/{fileName}";
        return publicPath;
    }

    public Task DeleteAsync(string photoUrl, CancellationToken ct = default)
    {
        var publicPrefix = _settings.PhotoPublicPath.TrimEnd('/');
        var relativePath = photoUrl.StartsWith(publicPrefix, StringComparison.OrdinalIgnoreCase)
            ? photoUrl[publicPrefix.Length..].TrimStart('/')
            : photoUrl.TrimStart('/');

        var absolutePath = Path.Combine(_env.ContentRootPath, _settings.PhotoStoragePath, relativePath);

        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        return Task.CompletedTask;
    }
}