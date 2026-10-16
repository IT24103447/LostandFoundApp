namespace AdminVerifyService.Spam;

public sealed class SpamReviewException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class SpamReviewTab
{
    public const string Active = "active";
    public const string Dismissed = "dismissed";
    public const string Solved = "solved";
}

public static class SpamReviewSort
{
    public const string Score = "score";
    public const string Date = "date";
}

public sealed record SpamRecordListQuery(
    IReadOnlyList<string> Statuses,
    bool SortByScore,
    DateTime? FlaggedFrom,
    DateTime? FlaggedBefore,
    IReadOnlyList<Guid> UserIds,
    int Page);

public sealed record SpamRecordRow(
    Guid Id,
    Guid UserId,
    int ScoreA,
    int ListingCount,
    string Status,
    DateTime FlaggedAt);

public sealed record SpamRecordPage(
    IReadOnlyList<SpamRecordRow> Items,
    bool HasMore);
