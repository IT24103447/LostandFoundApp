using AdminVerifyService.Spam;
using AdminVerifyService.UnitTests.Support;

namespace AdminVerifyService.UnitTests.ViewSpamRecords;

public sealed class ViewSpamRecordsTests
{
    [Theory]
    [InlineData("active", "NEEDS_REVIEW,UNDER_REVIEW,PENDING_SOLVE")]
    [InlineData("dismissed", "DISMISSED")]
    [InlineData("solved", "SOLVED")]
    public void TabsMapToTheirServerStatuses(string tab, string expected)
    {
        var query = SpamReviewQueryParser.Parse(tab, null, null, null, null, 1);
        Assert.Equal(expected.Split(','), query.Statuses);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("score", true)]
    [InlineData("date", false)]
    public void ServerSortingUsesScoreByDefault(string? sort, bool score) =>
        Assert.Equal(score, SpamReviewQueryParser.Parse(null, sort, null, null, null, 1).SortByScore);

    [Fact]
    public void DateFilterIncludesTheWholeEndDateAndDeduplicatesUsers()
    {
        var user = Guid.NewGuid();
        var query = SpamReviewQueryParser.Parse("active", "date", "2026-10-01", "2026-10-03", $"{user},{user}", 2);
        Assert.Equal(new DateTime(2026, 10, 1), query.FlaggedFrom);
        Assert.Equal(new DateTime(2026, 10, 4), query.FlaggedBefore);
        Assert.Equal(user, Assert.Single(query.UserIds));
        Assert.Equal(2, query.Page);
    }

    [Theory]
    [InlineData("wrong", "score", null, null, null, 1)]
    [InlineData("active", "wrong", null, null, null, 1)]
    [InlineData("active", "score", "2026-10-04", "2026-10-01", null, 1)]
    [InlineData("active", "score", "bad-date", null, null, 1)]
    [InlineData("active", "score", null, null, "bad-user-id", 1)]
    [InlineData("active", "score", null, null, null, 0)]
    [InlineData("active", "score", null, null, null, 100001)]
    public void InvalidFiltersReturn400(string tab, string sort, string? from, string? to, string? users, int page)
    {
        var error = Assert.Throws<SpamReviewException>(() => SpamReviewQueryParser.Parse(tab, sort, from, to, users, page));
        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public Task ListRequiresAdmin(bool authenticated, string? claim, bool expected) =>
        AdminPolicy.AssertAccessAsync("List", authenticated, claim, expected);
}
