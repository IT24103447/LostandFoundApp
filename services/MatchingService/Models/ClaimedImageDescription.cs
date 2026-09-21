namespace MatchingService.Models;

public sealed record ClaimedImageDescription(
    Guid Id,
    Guid LeaseToken,
    Guid ItemId,
    ItemType ItemType,
    ItemEventType SourceEventType,
    DateTime SourceOccurredAt,
    string BlobUrl,
    int Attempts);