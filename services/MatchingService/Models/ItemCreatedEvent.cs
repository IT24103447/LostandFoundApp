using System.Text.Json.Serialization;

namespace MatchingService.Models;

public sealed class ItemCreatedEvent
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; init; }

    [JsonPropertyName("lostItemId")]
    public Guid? LostItemId { get; init; }

    [JsonPropertyName("foundItemId")]
    public Guid? FoundItemId { get; init; }

    [JsonPropertyName("photoUrls")]
    public List<string> PhotoUrls { get; init; } = [];

    public Guid GetItemId(ItemType itemType) => itemType switch
    {
        ItemType.Lost when LostItemId is { } id && id != Guid.Empty => id,
        ItemType.Found when FoundItemId is { } id && id != Guid.Empty => id,
        _ => throw new InvalidDataException(
            "The item-created event does not contain a valid item ID for its topic.")
    };
}
