namespace ItemService.Models.Events;

public sealed class FoundItemCreatedEvent : BaseEvent
{
    public override string EventType => "found_item.created";
    // Details of the newly reported found item.
    public Guid FoundItemId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateOnly DateFound { get; init; }
    public string LocationFound { get; init; } = string.Empty;

    // Included for internal processing and matching.
    public string HiddenInformation { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string> PhotoUrls { get; init; } = [];
    public DateTime CreatedAt { get; init; }
}
