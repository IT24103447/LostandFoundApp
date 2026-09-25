using MatchingService.Matches;
using MatchingService.Tests.Integration;
using MySqlConnector;

namespace MatchingService.Tests.Matches;

/// <summary>
/// Story 3 (view potential matches) contract tests for MatchReadRepository's own SQL, against a real,
/// disposable MySQL database (Testcontainers, via ClaimServiceDbFixture, reused from Story 2). This is
/// the Matches page's read side: MatchReadServiceTests mocks IMatchReadRepository entirely, which
/// proves MatchReadService's own section/ownership logic but never runs a real query; this class is
/// the counterpart that proves the section filters (GetSectionFilter) and the count-plus-page
/// transaction actually behave correctly against real SQL, matching Story 1's pairing of a
/// mocked-repository test class with a separate real-database one. Rows are inserted directly with
/// arbitrary status/is_active combinations (ClaimRepository.CreateAsync, Story 2's write side, only
/// ever writes the two half-confirmed statuses, so it cannot produce the CONFIRMED/REJECTED/
/// AUTO_REJECTED_LOW_CONFIDENCE/inactive rows these tests need).
/// </summary>
public sealed class MatchReadRepositoryTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly IMatchReadRepository _repository;

    public MatchReadRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
        _repository = new MatchReadRepository(fixture.Connections);
    }

    private async Task<Guid> InsertMatchAsync(
        Guid lostReporterId,
        Guid finderId,
        string status,
        bool isActive = true,
        Guid? claimantId = null,
        DateTime? createdAt = null,
        string? deactivationReason = null,
        Guid? deactivatedItemId = null,
        string? deactivatedItemType = null)
    {
        var id = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        const string sql = """
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at,
                deactivated_at, deactivation_reason, deactivated_item_id, deactivated_item_type
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @claimantId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, @createdAt, @createdAt,
                @deactivatedAt, @deactivationReason, @deactivatedItemId, @deactivatedItemType
            );
            """;

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@foundItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@claimantId", claimantId ?? lostReporterId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@snapshot", snapshot);
        command.Parameters.AddWithValue("@createdAt", createdAt ?? DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "@deactivatedAt", deactivationReason is null ? DBNull.Value : (object)DateTime.UtcNow);
        command.Parameters.AddWithValue("@deactivationReason", (object?)deactivationReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@deactivatedItemId", (object?)deactivatedItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@deactivatedItemType", (object?)deactivatedItemType ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    // The "active" section is exactly the two half-confirmed statuses, and nothing else, even for the same user.
    [Fact]
    public async Task GetPageAsync_ActiveSection_ReturnsOnlyHalfConfirmedStatuses()
    {
        var userId = Guid.NewGuid();
        var lostReporterConfirmed = await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");
        var finderConfirmed = await InsertMatchAsync(userId, Guid.NewGuid(), "FINDER_CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "REJECTED");

        var page = await _repository.GetPageAsync(userId, "active", 1, 20, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(
            new[] { lostReporterConfirmed, finderConfirmed }.OrderBy(id => id),
            page.Items.Select(m => m.Id).OrderBy(id => id));
    }

    /* Scenario 4's "awaiting the other person's decision": a half-confirmed match belongs in this
       section only for the party who has NOT yet confirmed, whichever role they hold, and never for
       the party who already has. */
    [Fact]
    public async Task GetPageAsync_WaitingOnYouSection_IsRelativeToTheViewersOwnRoleOnEachMatch()
    {
        var userId = Guid.NewGuid();

        // The viewer is the lost reporter and the finder already confirmed: it's the viewer's turn.
        var turnAsLostReporter = await InsertMatchAsync(userId, Guid.NewGuid(), "FINDER_CONFIRMED");

        // The viewer is the finder and the lost reporter already confirmed: also the viewer's turn.
        var turnAsFinder = await InsertMatchAsync(Guid.NewGuid(), userId, "LOST_REPORTER_CONFIRMED");

        // The viewer is the lost reporter and already confirmed themselves: waiting on the other party, not them.
        await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");

        var page = await _repository.GetPageAsync(userId, "waiting-on-you", 1, 20, CancellationToken.None);

        Assert.Equal(
            new[] { turnAsLostReporter, turnAsFinder }.OrderBy(id => id),
            page.Items.Select(m => m.Id).OrderBy(id => id));
    }

    // The mirror image: "waiting-on-other" is a half-confirmed match where the viewer is the one who already acted.
    [Fact]
    public async Task GetPageAsync_WaitingOnOtherSection_IsTheMatchesTheViewerAlreadyConfirmed()
    {
        var userId = Guid.NewGuid();

        var alreadyConfirmedAsLostReporter = await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "FINDER_CONFIRMED");

        var page = await _repository.GetPageAsync(userId, "waiting-on-other", 1, 20, CancellationToken.None);

        var match = Assert.Single(page.Items);
        Assert.Equal(alreadyConfirmedAsLostReporter, match.Id);
    }

    [Theory]
    [InlineData("confirmed", "CONFIRMED")]
    [InlineData("rejected", "REJECTED")]
    public async Task GetPageAsync_TerminalSection_ReturnsOnlyThatStatus(string section, string status)
    {
        var userId = Guid.NewGuid();
        var expected = await InsertMatchAsync(userId, Guid.NewGuid(), status);
        await InsertMatchAsync(userId, Guid.NewGuid(), status == "CONFIRMED" ? "REJECTED" : "CONFIRMED");

        var page = await _repository.GetPageAsync(userId, section, 1, 20, CancellationToken.None);

        var match = Assert.Single(page.Items);
        Assert.Equal(expected, match.Id);
    }

    // "closed" is both terminal statuses together.
    [Fact]
    public async Task GetPageAsync_ClosedSection_ReturnsConfirmedAndRejectedTogether()
    {
        var userId = Guid.NewGuid();
        await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "REJECTED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");

        var page = await _repository.GetPageAsync(userId, "closed", 1, 20, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
    }

    // "all" ignores status entirely, but still never crosses to another user's matches.
    [Fact]
    public async Task GetPageAsync_AllSection_IgnoresStatusButStillScopesToTheRequestingUser()
    {
        var userId = Guid.NewGuid();
        await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        await InsertMatchAsync(Guid.NewGuid(), Guid.NewGuid(), "CONFIRMED"); // someone else's match entirely

        var page = await _repository.GetPageAsync(userId, "all", 1, 20, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
    }

    // A deactivated match (is_active = 0) is excluded from every section at the repository level too, not only by the service layer above it.
    [Fact]
    public async Task GetPageAsync_InactiveMatch_IsExcludedFromEverySection()
    {
        var userId = Guid.NewGuid();
        await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED", isActive: false);

        var page = await _repository.GetPageAsync(userId, "all", 1, 20, CancellationToken.None);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    // The count and the page it produces must agree even when there are more rows than one page holds.
    [Fact]
    public async Task GetPageAsync_MoreRowsThanOnePageSize_PagesCorrectlyWithAccurateTotalCount()
    {
        var userId = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-10);

        for (var i = 0; i < 3; i++)
        {
            await InsertMatchAsync(
                userId, Guid.NewGuid(), "CONFIRMED", createdAt: start.AddMinutes(i));
        }

        var firstPage = await _repository.GetPageAsync(userId, "all", page: 1, size: 2, CancellationToken.None);
        var secondPage = await _repository.GetPageAsync(userId, "all", page: 2, size: 2, CancellationToken.None);

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Single(secondPage.Items);

        // Newest first: page 1's two rows are the two most recently created.
        Assert.All(firstPage.Items, m => Assert.True(m.CreatedAt >= secondPage.Items[0].CreatedAt));
    }

    /* GetByIdAsync itself applies no status/ownership/is_active filter at all: that is
       MatchReadService's job (proven directly in MatchReadServiceTests), and this confirms the
       repository is the thin, unfiltered lookup those tests assume it is. */
    [Fact]
    public async Task GetByIdAsync_MatchRegardlessOfStatusOrActiveFlag_IsReturnedUnfiltered()
    {
        var id = await InsertMatchAsync(Guid.NewGuid(), Guid.NewGuid(), "REJECTED", isActive: false);

        var match = await _repository.GetByIdAsync(id, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("REJECTED", match!.Status);
        Assert.False(match.IsActive);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var match = await _repository.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(match);
    }

    // Scenario 8: an Auto Rejected Low Confidence match is excluded from every section, including "all" - the VisibleFilter's status allow-list applies before the section filter, not just within it.
    [Fact]
    public async Task GetPageAsync_AutoRejectedLowConfidenceMatch_IsExcludedFromEverySectionIncludingAll()
    {
        var userId = Guid.NewGuid();
        var visible = await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "AUTO_REJECTED_LOW_CONFIDENCE");

        var page = await _repository.GetPageAsync(userId, "all", 1, 20, CancellationToken.None);

        var match = Assert.Single(page.Items);
        Assert.Equal(visible, match.Id);
    }

    // Scenario 10: a user who has never had a match at all gets an empty page, not an error or a null reference.
    [Fact]
    public async Task GetPageAsync_UserHasNoMatchesAtAll_ReturnsEmptyPageWithZeroTotalCount()
    {
        var userId = Guid.NewGuid();

        var page = await _repository.GetPageAsync(userId, "all", 1, 20, CancellationToken.None);

        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
    }

    // ---- Story 7: the "deactivated" section, and deactivated matches folding into "closed"/"all" ----

    [Fact]
    public async Task GetPageAsync_DeactivatedSection_ReturnsOnlyDeactivatedHalfConfirmedMatches()
    {
        var userId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var deactivated = await InsertMatchAsync(
            userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED", isActive: false,
            deactivationReason: "ITEM_RESOLVED", deactivatedItemId: itemId, deactivatedItemType: "LOST");
        await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");

        var page = await _repository.GetPageAsync(userId, "deactivated", 1, 20, CancellationToken.None);

        var match = Assert.Single(page.Items);
        Assert.Equal(deactivated, match.Id);
        Assert.Equal("ITEM_RESOLVED", match.DeactivationReason);
        Assert.Equal(itemId, match.DeactivatedItemId);
        Assert.Equal("LOST", match.DeactivatedItemType);
    }

    [Theory]
    [InlineData("ITEM_RESOLVED")]
    [InlineData("ITEM_DELETED")]
    [InlineData("MATCH_CONFIRMED_ELSEWHERE")]
    public async Task GetPageAsync_DeactivatedSection_IncludesEveryDeactivationReason(string reason)
    {
        var userId = Guid.NewGuid();
        var deactivated = await InsertMatchAsync(
            userId, Guid.NewGuid(), "FINDER_CONFIRMED", isActive: false, deactivationReason: reason);

        var page = await _repository.GetPageAsync(userId, "deactivated", 1, 20, CancellationToken.None);

        Assert.Equal(deactivated, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetPageAsync_DeactivatedRejectedMatch_IsNotShownAsDeactivated()
    {
        // A match already REJECTED before its item was later resolved/deleted is a terminal outcome
        // in its own right, not a "history" case this section is for - ItemLifecycleRepository itself
        // never deactivates a REJECTED match, but this proves the read-side filter agrees.
        var userId = Guid.NewGuid();
        await InsertMatchAsync(
            userId, Guid.NewGuid(), "REJECTED", isActive: false, deactivationReason: "ITEM_RESOLVED");

        var page = await _repository.GetPageAsync(userId, "deactivated", 1, 20, CancellationToken.None);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task GetPageAsync_ClosedSection_IncludesDeactivatedMatchesAlongsideConfirmedAndRejected()
    {
        var userId = Guid.NewGuid();
        var confirmed = await InsertMatchAsync(userId, Guid.NewGuid(), "CONFIRMED");
        var rejected = await InsertMatchAsync(userId, Guid.NewGuid(), "REJECTED");
        var deactivated = await InsertMatchAsync(
            userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED", isActive: false,
            deactivationReason: "ITEM_DELETED");

        var page = await _repository.GetPageAsync(userId, "closed", 1, 20, CancellationToken.None);

        Assert.Equal(
            new[] { confirmed, rejected, deactivated }.OrderBy(id => id),
            page.Items.Select(m => m.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task GetPageAsync_AllSection_IncludesDeactivatedMatches()
    {
        var userId = Guid.NewGuid();
        var active = await InsertMatchAsync(userId, Guid.NewGuid(), "LOST_REPORTER_CONFIRMED");
        var deactivated = await InsertMatchAsync(
            userId, Guid.NewGuid(), "FINDER_CONFIRMED", isActive: false,
            deactivationReason: "MATCH_CONFIRMED_ELSEWHERE");

        var page = await _repository.GetPageAsync(userId, "all", 1, 20, CancellationToken.None);

        Assert.Equal(
            new[] { active, deactivated }.OrderBy(id => id),
            page.Items.Select(m => m.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task GetByIdAsync_DeactivatedMatch_ReturnsTheAuditFields()
    {
        var lostReporterId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            lostReporterId, Guid.NewGuid(), "FINDER_CONFIRMED", isActive: false,
            deactivationReason: "ITEM_RESOLVED", deactivatedItemId: itemId, deactivatedItemType: "LOST");

        var match = await _repository.GetByIdAsync(matchId, CancellationToken.None);

        Assert.NotNull(match);
        Assert.False(match!.IsActive);
        Assert.Equal("ITEM_RESOLVED", match.DeactivationReason);
        Assert.Equal(itemId, match.DeactivatedItemId);
        Assert.Equal("LOST", match.DeactivatedItemType);
        Assert.NotNull(match.DeactivatedAt);
    }
}
