using System.Text.Json;

namespace AdminVerifyService.Listings;

public static class ListingEventParser
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static ListingEvent? Parse(string topic, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        EventPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<EventPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        var isLost = topic.Contains(".lost_item.", StringComparison.Ordinal);
        var listingId = isLost ? payload.LostItemId : payload.FoundItemId;
        var postedAt = topic.EndsWith(".created", StringComparison.Ordinal)
            ? payload.CreatedAt
            : payload.UpdatedAt;

        if (payload.EventId == Guid.Empty ||
            payload.UserId == Guid.Empty ||
            listingId is null ||
            listingId == Guid.Empty ||
            postedAt is null ||
            postedAt == default(DateTime))
        {
            return null;
        }

        return new ListingEvent(
            payload.EventId,
            topic,
            listingId.Value,
            isLost ? ListingType.Lost : ListingType.Found,
            payload.UserId,
            ToUtc(postedAt.Value));
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private sealed class EventPayload
    {
        public Guid EventId { get; init; }
        public Guid UserId { get; init; }
        public Guid? LostItemId { get; init; }
        public Guid? FoundItemId { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }
}
