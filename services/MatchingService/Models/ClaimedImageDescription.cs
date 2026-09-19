namespace MatchingService.Models;

public sealed record ClaimedImageDescription(
    Guid Id,
    Guid LeaseToken,
    string BlobUrl,
    int Attempts);