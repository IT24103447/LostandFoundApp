namespace MatchingService.Models;

public sealed record CurrentImageDescription(
    Guid DescriptionId,
    Guid ItemId,
    ItemType ItemType,
    string Description,
    string? AttributesJson,
    DateTime SourceOccurredAt,
    DateTime ProcessedAt);