namespace ItemService.Models.Events;

public sealed class LostItemCreatedEvent : BaseEvent
{
    public override string EventType => "lost_item.created";

    public Guid LostItemId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateOnly DateLost { get; init; }
    public string LastKnownLocation { get; init; } = string.Empty;
    public string HiddenInformation { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string> PhotoUrls { get; init; } = [];
    public DateTime CreatedAt { get; init; }
}
