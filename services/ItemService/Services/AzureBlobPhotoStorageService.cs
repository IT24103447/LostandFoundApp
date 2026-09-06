using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ItemService.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ItemService.Services;

public class AzureBlobPhotoStorageService : IPhotoStorageService
{
    private readonly BlobStorageSettings _settings;
    private readonly ILogger<AzureBlobPhotoStorageService> _logger;
    private readonly Lazy<Task<BlobContainerClient>> _containerClient;

    public AzureBlobPhotoStorageService(
        IOptions<BlobStorageSettings> settings,
        ILogger<AzureBlobPhotoStorageService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            throw new InvalidOperationException(
                "BlobStorage:ConnectionString is not configured. " +
                "AzureBlobPhotoStorageService should only be registered when it's set — " +
                "see the fallback logic in Program.cs.");
        }

        _containerClient = new Lazy<Task<BlobContainerClient>>(InitializeContainerAsync);
    }

    private async Task<BlobContainerClient> InitializeContainerAsync()
    {
        var serviceClient = new BlobServiceClient(_settings.ConnectionString);
        var container = serviceClient.GetBlobContainerClient(_settings.ContainerName);

        await container.CreateIfNotExistsAsync(PublicAccessType.Blob);

        _logger.LogInformation("Blob container '{Container}' ready.", _settings.ContainerName);
        return container;
    }

    public async Task<string> SaveAsync(Guid lostItemId, IFormFile photo, CancellationToken ct = default)
    {
        var container = await _containerClient.Value;

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

        var blobName = $"{lostItemId}/{Guid.NewGuid()}{extension}";
        var blobClient = container.GetBlobClient(blobName);

        await using var stream = photo.OpenReadStream();
        await blobClient.UploadAsync(
            stream,
            new BlobHttpHeaders { ContentType = photo.ContentType },
            cancellationToken: ct);

        _logger.LogDebug("Uploaded photo for lost item {LostItemId} to blob {BlobName}.", lostItemId, blobName);

        return blobClient.Uri.ToString();
    }
}
