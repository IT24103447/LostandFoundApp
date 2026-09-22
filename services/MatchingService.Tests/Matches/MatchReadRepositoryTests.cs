using MatchingService.Matches;
using MatchingService.Tests.Integration;
using MySqlConnector;

namespace MatchingService.Tests.Matches;

/// <summary>
/// Story 2 (LF-173) contract tests for MatchReadRepository's own SQL, against a real, disposable
/// MySQL database (Testcontainers, via ClaimServiceDbFixture). MatchReadServiceTests mocks
/// IMatchReadRepository entirely, which proves MatchReadService's own section/ownership logic but
/// never runs a real query; this class is the counterpart that proves the six section filters
/// (GetSectionFilter) and the count-plus-page transaction actually behave correctly against real SQL,
/// matching Story 1's pairing of a mocked-repository test class with a separate real-database one.
/// Rows are inserted directly with arbitrary status/is_active combinations (ClaimRepository.CreateAsync
/// only ever writes the two half-confirmed statuses, so it cannot produce the CONFIRMED/REJECTED/
/// inactive rows these tests need).
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
        DateTime? createdAt = null)
    {
        var id = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        const string sql = """
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @claimantId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, @createdAt, @createdAt
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
}
