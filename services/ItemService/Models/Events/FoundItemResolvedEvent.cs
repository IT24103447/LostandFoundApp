namespace ItemService.Models.Events;

// Carries the FULL current item state, including HiddenInformation, so the
// Matching Service can correctly remove or finalize any related match records.
public sealed class FoundItemResolvedEvent : BaseEvent
{
    public override string EventType => "found_item.resolved";

    public Guid FoundItemId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateOnly DateFound { get; init; }
    public string LocationFound { get; init; } = string.Empty;
    public string HiddenInformation { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public List<string> PhotoUrls { get; init; } = [];
    public DateTime ResolvedAt { get; init; }
}