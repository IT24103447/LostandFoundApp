using MatchingService.Models;

namespace MatchingService.Services;

public interface IImageDescriptionGenerator
{
    Task<GeneratedImageDescription> GenerateAsync(
        string blobUrl,
        CancellationToken cancellationToken);
}