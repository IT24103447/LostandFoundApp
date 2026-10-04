using System.Globalization;

namespace AdminVerifyService.Spam;

public static class SpamReviewQueryParser
{
    public const int MaxUserIds = 100;
    public const int MaxPage = 100000;

    private const string DateFormat = "yyyy-MM-dd";

    public static SpamRecordListQuery Parse(
        string? tab,
        string? sort,
        string? from,
        string? to,
        string? userIds,
        int page)
    {
        if (page is < 1 or > MaxPage)
        {
            throw BadRequest("Invalid page number.");
        }

        var flaggedFrom = ParseDate(from, "from");
        var flaggedTo = ParseDate(to, "to");

        if (flaggedFrom > flaggedTo)
        {
            throw BadRequest("The from date must not be after the to date.");
        }

        return new SpamRecordListQuery(
            ParseTab(tab),
            ParseSort(sort),
            flaggedFrom,
            flaggedTo?.AddDays(1),
            ParseUserIds(userIds),
            page);
    }

    private static string[] ParseTab(string? tab) => (tab ?? SpamReviewTab.Active).ToLowerInvariant() switch
    {
        SpamReviewTab.Active =>
            [SpamRecordStatus.NeedsReview, SpamRecordStatus.UnderReview, SpamRecordStatus.PendingSolve],
        SpamReviewTab.Dismissed => [SpamRecordStatus.Dismissed],
        SpamReviewTab.Solved => [SpamRecordStatus.Solved],
        _ => throw BadRequest("Tab must be active, dismissed or solved.")
    };

    private static bool ParseSort(string? sort) => (sort ?? SpamReviewSort.Score).ToLowerInvariant() switch
    {
        SpamReviewSort.Score => true,
        SpamReviewSort.Date => false,
        _ => throw BadRequest("Sort must be score or date.")
    };

    private static DateTime? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value.Trim(),
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
        {
            throw BadRequest($"The {name} date must use the format {DateFormat}.");
        }

        return date;
    }

    private static Guid[] ParseUserIds(string? userIds)
    {
        if (string.IsNullOrWhiteSpace(userIds))
        {
            return [];
        }

        var parts = userIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length > MaxUserIds)
        {
            throw BadRequest($"No more than {MaxUserIds} user IDs can be filtered at once.");
        }

        var ids = new Guid[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!Guid.TryParse(parts[i], out ids[i]))
            {
                throw BadRequest("User IDs must be valid IDs.");
            }
        }

        return ids.Distinct().ToArray();
    }

    private static SpamReviewException BadRequest(string message) =>
        new(StatusCodes.Status400BadRequest, message);
}
