using System.Text.Json;
using MatchingService.Claims;

namespace MatchingService.Reevaluation;

public sealed record MatchItemSnapshotEvent(
    Guid EventId,
    string ItemType,
    Guid ItemId,
    DateTime OccurredAt,
    bool IsUpdate,
    ItemReport Report);

public sealed class MatchReevaluationEventHandler(
    MatchReevaluationRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task HandleAsync(
        string topic,
        string json,
        CancellationToken cancellationToken)
    {
        var itemType = topic.Contains(
            ".lost_item.",
            StringComparison.Ordinal)
                ? "LOST"
                : topic.Contains(
                    ".found_item.",
                    StringComparison.Ordinal)
                        ? "FOUND"
                        : throw new InvalidDataException(
                            "Unsupported item event topic.");

        var isUpdate = topic.EndsWith(
            ".updated",
            StringComparison.Ordinal);

        if (!isUpdate &&
            !topic.EndsWith(
                ".created",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unsupported item event type.");
        }

        var source = JsonSerializer.Deserialize<ItemEventPayload>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException(
                "The item event is empty.");

        var itemId = itemType == "LOST"
            ? source.LostItemId
            : source.FoundItemId;

        if (source.EventId == Guid.Empty ||
            source.UserId == Guid.Empty ||
            !itemId.HasValue ||
            itemId.Value == Guid.Empty ||
            source.Timestamp == default ||
            string.IsNullOrWhiteSpace(source.Title) ||
            string.IsNullOrWhiteSpace(source.Category) ||
            string.IsNullOrWhiteSpace(source.Description) ||
            string.IsNullOrWhiteSpace(source.Status))
        {
            throw new InvalidDataException(
                "The item event is missing required public fields.");
        }

        var report = new ItemReport
        {
            Id = itemId.Value,
            UserId = source.UserId,
            Title = source.Title.Trim(),
            Category = source.Category.Trim(),
            Description = source.Description.Trim(),
            Status = source.Status.Trim(),
            DateLost = source.DateLost ?? string.Empty,
            DateFound = source.DateFound ?? string.Empty,
            LastKnownLocation =
                source.LastKnownLocation ?? string.Empty,
            LocationFound =
                source.LocationFound ?? string.Empty,
            PhotoUrls = source.PhotoUrls ?? []
        };

        await repository.RecordEventAsync(
            new MatchItemSnapshotEvent(
                source.EventId,
                itemType,
                report.Id,
                source.Timestamp.ToUniversalTime(),
                isUpdate,
                report),
            cancellationToken);
    }

    // HiddenInformation is deliberately not represented or stored here.
    private sealed class ItemEventPayload
    {
        public Guid EventId { get; init; }
        public DateTime Timestamp { get; init; }
        public Guid UserId { get; init; }
        public Guid? LostItemId { get; init; }
        public Guid? FoundItemId { get; init; }
        public string? Title { get; init; }
        public string? Category { get; init; }
        public string? Description { get; init; }
        public string? Status { get; init; }
        public string? DateLost { get; init; }
        public string? DateFound { get; init; }
        public string? LastKnownLocation { get; init; }
        public string? LocationFound { get; init; }
        public List<string>? PhotoUrls { get; init; }
    }
}