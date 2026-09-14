namespace ItemService.Models.Events;

// Carries the FULL current item state, including HiddenInformation, so the
// Matching Service can correctly remove or finalize any related match records.
public sealed class LostItemResolvedEvent : BaseEvent
{
    public override string EventType => "lost_item.resolved";

    public Guid LostItemId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateOnly DateLost { get; init; }
    public string LastKnownLocation { get; init; } = string.Empty;
    public string HiddenInformation { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string> PhotoUrls { get; init; } = [];
    public DateTime ResolvedAt { get; init; }
}