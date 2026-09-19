namespace MatchingService.Models;

public sealed record ImageDescriptionRecord(
    Guid Id,
    Guid SourceEventId,
    string PhotoKey,
    Guid ItemId,
    ItemType ItemType,
    string BlobUrl,
    ImageProcessingStatus ProcessingStatus,
    int Attempts,
    DateTime? NextRetryAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
