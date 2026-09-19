using MatchingService.Models;

namespace MatchingService.Repositories;

public interface IImageDescriptionRepository
{
    Task<bool> CreatePendingAsync(
        ImageDescriptionRecord record,
        CancellationToken cancellationToken);

    Task<ClaimedImageDescription?> ClaimNextAsync(
        int maxAttempts,
        int leaseSeconds,
        CancellationToken cancellationToken);

    Task<bool> CompleteAsync(
        ClaimedImageDescription job,
        GeneratedImageDescription description,
        string modelName,
        CancellationToken cancellationToken);

    Task<bool> RecordFailureAsync(
        ClaimedImageDescription job,
        string errorCode,
        DateTime? nextRetryAt,
        CancellationToken cancellationToken);
}