// changed during sprint 3 by dev
namespace ItemService.Models.Events;

public sealed record MatchConfirmedIntegrationEvent(
    int SchemaVersion,
    string EventType,
    Guid EventId,
    Guid MatchId,
    Guid LostItemId,
    Guid FoundItemId,
    Guid LostReporterId,
    Guid FinderId,
    DateTime ConfirmedAt)
{
    public bool IsValid() =>
        SchemaVersion == 1 && EventType == "match.confirmed" &&
        EventId != Guid.Empty && MatchId != Guid.Empty &&
        LostItemId != Guid.Empty && FoundItemId != Guid.Empty &&
        LostReporterId != Guid.Empty && FinderId != Guid.Empty &&
        LostItemId != FoundItemId && LostReporterId != FinderId &&
        ConfirmedAt != default;
}
