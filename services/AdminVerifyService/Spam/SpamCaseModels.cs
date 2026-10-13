using System.Text.Json.Serialization;

namespace AdminVerifyService.Spam;

public static class KickStatus
{
    public const string NotRequested = "NOT_REQUESTED";
    public const string Pending = "PENDING";
    public const string Kicked = "KICKED";
    public const string Failed = "FAILED";
}

public static class SolveResult
{
    public const string Deleted = "DELETED";
    public const string Skipped = "SKIPPED";
    public const string Failed = "FAILED";
}

public sealed record SolveRequest([property: JsonRequired] bool Kick, [property: JsonRequired] bool Resume);

public sealed record ResultRequest(string? Result);

public sealed record SpamRecordListing(
    Guid ListingId,
    string ListingType,
    DateTime PostedAt,
    string? SolveResult);

public sealed record SpamRecordDetail(
    Guid Id,
    Guid UserId,
    int ScoreA,
    int ListingCount,
    string Status,
    DateTime FlaggedAt,
    string KickStatus,
    IReadOnlyList<SpamRecordListing> Listings);
