using AdminVerifyService.Spam;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-87 pure, no-network coverage of SpamReviewQueryParser.Parse - the query-string to
/// typed-query translation for the Spam Review list API: tab -> statuses, sort order flag, the
/// from/to date range (from inclusive, to exclusive by adding one day), user-id filters and page
/// bounds. A pure parser, so no database, no Kafka and no Docker - this class always runs in CI.
///
/// Where the parser is deliberately lenient is pinned as behaviour: tab and sort comparisons are
/// case-insensitive but not whitespace-trimmed, and date parsing accepts only the exact zero-padded
/// yyyy-MM-dd form under an invariant culture.
/// </summary>
public sealed class SpamReviewQueryParserTests
{
    private const string ActiveTabs =
        SpamRecordStatus.NeedsReview + "," + SpamRecordStatus.UnderReview + "," + SpamRecordStatus.PendingSolve;

    private static readonly Guid UserId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SpamRecordListQuery Parse(
        string? tab = null,
        string? sort = null,
        string? from = null,
        string? to = null,
        string? userIds = null,
        int page = 1) =>
        SpamReviewQueryParser.Parse(tab, sort, from, to, userIds, page);

    private static void AssertBadRequest(Action parse, string message)
    {
        var exception = Assert.Throws<SpamReviewException>(parse);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(message, exception.Message);
    }

    private static string[] Statuses(string joined) => joined.Split(',');

    // ---- defaults ----

    [Fact] // LF-87 AC: no query parameters at all means the active queue, score order, no filters, first page.
    public void Parse_Defaults_AreActiveScoreNoFiltersPageOne()
    {
        var query = Parse();

        Assert.Equal(Statuses(ActiveTabs), query.Statuses);
        Assert.True(query.SortByScore);
        Assert.Null(query.FlaggedFrom);
        Assert.Null(query.FlaggedBefore);
        Assert.Empty(query.UserIds);
        Assert.Equal(1, query.Page);
    }

    // ---- tabs ----

    public static TheoryData<string, string> TabCases() => new()
    {
        { SpamReviewTab.Active, ActiveTabs },
        { "ACTIVE", ActiveTabs }, // upper case is accepted (lowered before the switch)
        { SpamReviewTab.Dismissed, SpamRecordStatus.Dismissed },
        { SpamReviewTab.Solved, SpamRecordStatus.Solved }
    };

    [Theory]
    [MemberData(nameof(TabCases))]
    public void Parse_Tab_MapsToTheStatusesForThatTab(string tab, string expectedStatuses)
    {
        var query = Parse(tab: tab);

        Assert.Equal(Statuses(expectedStatuses), query.Statuses);
    }

    // ---- sort ----

    [Theory]
    [InlineData(null, true)]     // default: score order
    [InlineData("score", true)]
    [InlineData("SCORE", true)]  // upper case is accepted
    [InlineData("date", false)]
    public void Parse_Sort_SetsTheScoreVsDateOrder(string? sort, bool expected)
    {
        var query = Parse(sort: sort);

        Assert.Equal(expected, query.SortByScore);
    }

    // ---- date range ----

    [Fact] // LF-87 AC: the to date is exclusive - the query hands the repository to+1 day.
    public void Parse_DateRange_FromIsInclusive_ToIsExclusivePlusOneDay()
    {
        var query = Parse(from: "2026-05-01", to: "2026-05-03");

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), query.FlaggedFrom);
        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), query.FlaggedBefore);
    }

    [Fact]
    public void Parse_FromOnly_SetsFlaggedFrom()
    {
        var query = Parse(from: "2026-05-01");

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), query.FlaggedFrom);
        Assert.Null(query.FlaggedBefore);
    }

    [Fact]
    public void Parse_ToOnly_SetsFlaggedBeforePlusOneDay()
    {
        var query = Parse(to: "2026-05-03");

        Assert.Null(query.FlaggedFrom);
        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), query.FlaggedBefore);
    }

    [Fact] // A single-day view is legal: from == to is not "after".
    public void Parse_SameDayRange_IsAllowed()
    {
        var query = Parse(from: "2026-05-04", to: "2026-05-04");

        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), query.FlaggedFrom);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), query.FlaggedBefore);
    }

    [Fact] // Whitespace-only dates are treated as absent, like null.
    public void Parse_WhitespaceDates_AreTreatedAsAbsent()
    {
        var query = Parse(from: "   ", to: "  ");

        Assert.Null(query.FlaggedFrom);
        Assert.Null(query.FlaggedBefore);
    }

    // ---- user-id filters ----

    [Fact] // LF-87 AC: duplicates collapse, entries are trimmed and empty fragments are dropped.
    public void Parse_UserIds_AreTrimmedDeduplicatedAndDropEmpties()
    {
        var query = Parse(userIds: $"  {UserId1},,{UserId2} , {UserId1} ,");

        Assert.Equal(
            new[] { UserId1, UserId2 },
            query.UserIds);
    }

    [Fact] // The cap is inclusive: exactly 100 user ids is accepted.
    public void Parse_ExactlyMaxUserIds_IsAccepted()
    {
        var ids = Enumerable.Range(0, SpamReviewQueryParser.MaxUserIds)
            .Select(_ => Guid.NewGuid());

        var query = Parse(userIds: string.Join(",", ids));

        Assert.Equal(SpamReviewQueryParser.MaxUserIds, query.UserIds.Count);
    }

    // ---- page bounds ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)] // MaxPage + 1
    public void Parse_OutOfRangePage_Throws(int page)
    {
        AssertBadRequest(
            () => Parse(page: page),
            "Invalid page number.");
    }

    [Fact] // The upper bound itself is legal.
    public void Parse_MaxPageBoundary_IsAccepted() =>
        Assert.Equal(SpamReviewQueryParser.MaxPage, Parse(page: SpamReviewQueryParser.MaxPage).Page);

    // ---- rejected input ----

    [Fact]
    public void Parse_FromDateInvalidFormat_Throws()
    {
        AssertBadRequest(
            () => Parse(from: "2026-5-1"),
            "The from date must use the format yyyy-MM-dd.");
    }

    [Fact]
    public void Parse_ToDateInvalidFormat_Throws()
    {
        AssertBadRequest(
            () => Parse(to: "01-05-2026"),
            "The to date must use the format yyyy-MM-dd.");
    }

    [Fact]
    public void Parse_FromAfterTo_Throws()
    {
        AssertBadRequest(
            () => Parse(from: "2026-05-05", to: "2026-05-01"),
            "The from date must not be after the to date.");
    }

    [Fact]
    public void Parse_UnknownTab_Throws()
    {
        AssertBadRequest(
            () => Parse(tab: "reviewing"),
            "Tab must be active, dismissed or solved.");
    }

    [Fact] // Tab/sort are case-insensitive but NOT trimmed - a padded value is rejected.
    public void Parse_TabNotTrimmed_Throws()
    {
        AssertBadRequest(
            () => Parse(tab: " active "),
            "Tab must be active, dismissed or solved.");
    }

    [Fact]
    public void Parse_UnknownSort_Throws()
    {
        AssertBadRequest(
            () => Parse(sort: "popularity"),
            "Sort must be score or date.");
    }

    [Fact]
    public void Parse_TooManyUserIds_Throws()
    {
        var ids = Enumerable.Range(0, SpamReviewQueryParser.MaxUserIds + 1)
            .Select(_ => Guid.NewGuid());

        AssertBadRequest(
            () => Parse(userIds: string.Join(",", ids)),
            $"No more than {SpamReviewQueryParser.MaxUserIds} user IDs can be filtered at once.");
    }

    [Fact]
    public void Parse_InvalidUserId_Throws()
    {
        AssertBadRequest(
            () => Parse(userIds: $"{UserId1},not-a-guid"),
            "User IDs must be valid IDs.");
    }
}