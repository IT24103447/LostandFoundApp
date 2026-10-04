namespace AdminVerifyService.Listings;

public static class ListingType
{
    public const string Lost = "LOST";
    public const string Found = "FOUND";
}

public sealed record ListingEvent(
    Guid EventId,
    string Topic,
    Guid ListingId,
    string ListingType,
    Guid UserId,
    DateTime PostedAt);
