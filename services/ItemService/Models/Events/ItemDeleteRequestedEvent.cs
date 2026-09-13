namespace ItemService.Models.Events;

// Tells the Matching Service that this item was soft-deleted by its reporter,
// so it can remove or invalidate any related match records.
public sealed class ItemDeleteRequestedEvent : BaseEvent
{
    public override string EventType => "item.delete_requested";

    public Guid ItemId { get; init; }
    public string ItemType { get; init; } = string.Empty; // "LOST" or "FOUND"
}
