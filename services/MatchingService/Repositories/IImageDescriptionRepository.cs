using MatchingService.Models;

namespace MatchingService.Repositories;

public interface IImageDescriptionRepository
{
    Task<bool> CreatePendingAsync(
        ImageDescriptionRecord record,
        CancellationToken cancellationToken);
}
