using MatchingService.Claims;
using MatchingService.Matches;
using MatchingService.Tests.Integration;
using MySqlConnector;

namespace MatchingService.Tests.Matches;

/// <summary>
/// Story 5 (finder confirms/rejects a match) contract tests for FinderDecisionRepository, the mirror
/// image of LostReporterDecisionRepositoryTests (Story 4): the finder decides instead of the lost
/// reporter, the turn status is LOST_REPORTER_CONFIRMED instead of FINDER_CONFIRMED, and the contact
/// revealed on confirmation is the lost reporter's instead of the finder's. Against a real, disposable
/// MySQL database (Testcontainers, via ClaimServiceDbFixture, reused from Story 2/3/4).
/// </summary>
public sealed class FinderDecisionRepositoryTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly FinderDecisionRepository _repository;

    public FinderDecisionRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
        _repository = new FinderDecisionRepository(fixture.Connections, TimeProvider.System);
    }

    private async Task<(Guid MatchId, Guid LostItemId, Guid FoundItemId)> InsertMatchAsync(
        Guid lostReporterId,
        Guid finderId,
        string status,
        bool isActive = true,
        Guid? lostItemId = null,
        Guid? foundItemId = null,
        string? finderEmail = null,
        string? finderPhone = null,
        string? lostReporterEmail = null,
        string? lostReporterPhone = null)
    {
        var id = Guid.NewGuid();
        var actualLostItemId = lostItemId ?? Guid.NewGuid();
        var actualFoundItemId = foundItemId ?? Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        const string sql = """
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, finder_email, finder_phone,
                lost_reporter_email, lost_reporter_phone, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, @finderEmail, @finderPhone,
                @lostReporterEmail, @lostReporterPhone, @now, @now
            );
            """;

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", actualLostItemId);
        command.Parameters.AddWithValue("@foundItemId", actualFoundItemId);
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@snapshot", snapshot);
        command.Parameters.AddWithValue("@finderEmail", (object?)finderEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@finderPhone", (object?)finderPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@lostReporterEmail", (object?)lostReporterEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@lostReporterPhone", (object?)lostReporterPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);

        await command.ExecuteNonQueryAsync();
        return (id, actualLostItemId, actualFoundItemId);
    }

    private async Task<(string Status, bool IsActive)> ReadMatchStateAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, is_active FROM matches WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetBoolean(1));
    }

    private async Task<int> CountAuditRowsAsync(
        Guid matchId, string actorRole, string action, string previousStatus, string newStatus)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT COUNT(*) FROM match_actions
            WHERE match_id = @matchId AND actor_role = @role AND action = @action
              AND previous_status = @previousStatus AND new_status = @newStatus
              AND created_at IS NOT NULL;
            """, connection);
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@role", actorRole);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@previousStatus", previousStatus);
        command.Parameters.AddWithValue("@newStatus", newStatus);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountOutboxRowsForMatchAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT COUNT(*) FROM match_confirmation_outbox WHERE match_id = @matchId;", connection);
        command.Parameters.AddWithValue("@matchId", matchId);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DecideAsync_MatchNotFound_ThrowsNotFound()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(Guid.NewGuid(), Guid.NewGuid(), confirm: true,
                "finder@example.com", "+94771234567", CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }

    // Scenario 4: neither the lost reporter nor an unrelated user can decide - only this match's finder.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecideAsync_CallerIsNotFinder_ThrowsForbidden(bool callerIsLostReporter)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(lostReporterId, finderId, "LOST_REPORTER_CONFIRMED");

        var caller = callerIsLostReporter ? lostReporterId : Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(matchId, caller, confirm: true,
                "finder@example.com", "+94771234567", CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
    }

    // Scenario 5: any status other than Lost Reporter Confirmed, or a deactivated match, is not the finder's turn.
    [Theory]
    [InlineData("FINDER_CONFIRMED", true)]
    [InlineData("CONFIRMED", true)]
    [InlineData("REJECTED", true)]
    [InlineData("LOST_REPORTER_CONFIRMED", false)]
    public async Task DecideAsync_NotAtLostReporterConfirmedOrInactive_ThrowsConflict(string status, bool isActive)
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), finderId, status, isActive);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(matchId, finderId, confirm: true,
                "finder@example.com", "+94771234567", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    // Defensive: confirming without a usable email/phone is rejected the same way ClaimService.SubmitAsync already guards claim submission.
    [Fact]
    public async Task DecideAsync_ConfirmWithoutContactDetails_ThrowsConflict()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(matchId, finderId, confirm: true,
                finderEmail: null, finderPhone: "+94771234567", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    // Scenario 2, plus the audit-row bullet of the Definition of Done: confirming moves the match to
    // Confirmed, stores the finder's own contact details, and writes an audit row for it.
    [Fact]
    public async Task DecideAsync_ConfirmAtLostReporterConfirmed_MovesToConfirmedStoresContactAndWritesAudit()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        var result = await _repository.DecideAsync(
            matchId, finderId, confirm: true,
            "finder@example.com", "+94771234567", CancellationToken.None);

        Assert.Equal("CONFIRMED", result.Status);

        var (status, isActive) = await ReadMatchStateAsync(matchId);
        Assert.Equal("CONFIRMED", status);
        Assert.True(isActive);

        Assert.Equal(1, await CountAuditRowsAsync(
            matchId, "FOUND", "CONFIRM", "LOST_REPORTER_CONFIRMED", "CONFIRMED"));
    }

    // Scenario 3, plus the audit-row bullet: rejecting moves the match to Rejected and writes an audit row.
    [Fact]
    public async Task DecideAsync_RejectAtLostReporterConfirmed_MovesToRejectedAndWritesAudit()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        var result = await _repository.DecideAsync(
            matchId, finderId, confirm: false,
            finderEmail: null, finderPhone: null, CancellationToken.None);

        Assert.Equal("REJECTED", result.Status);

        var (status, _) = await ReadMatchStateAsync(matchId);
        Assert.Equal("REJECTED", status);

        Assert.Equal(1, await CountAuditRowsAsync(
            matchId, "FOUND", "REJECT", "LOST_REPORTER_CONFIRMED", "REJECTED"));
    }

    // Scenario 3: "no further action is possible on it" - a Rejected match is no longer the finder's turn either.
    [Fact]
    public async Task DecideAsync_AfterReject_NoFurtherActionIsPossible()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        await _repository.DecideAsync(matchId, finderId, confirm: false,
            finderEmail: null, finderPhone: null, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(matchId, finderId, confirm: true,
                "finder@example.com", "+94771234567", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task DecideAsync_Confirm_EnqueuesConfirmationOutboxEvent()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        await _repository.DecideAsync(matchId, finderId, confirm: true,
            "finder@example.com", "+94771234567", CancellationToken.None);

        Assert.Equal(1, await CountOutboxRowsForMatchAsync(matchId));
    }

    [Fact]
    public async Task DecideAsync_Reject_DoesNotEnqueueConfirmationOutboxEvent()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED");

        await _repository.DecideAsync(matchId, finderId, confirm: false,
            finderEmail: null, finderPhone: null, CancellationToken.None);

        Assert.Equal(0, await CountOutboxRowsForMatchAsync(matchId));
    }

    // The outbox's own cross-match guard: a report cannot end up confirmed in two matches at once.
    [Fact]
    public async Task DecideAsync_ConfirmWhenSameFoundItemAlreadyConfirmedElsewhere_ThrowsConflict()
    {
        var sharedFoundItemId = Guid.NewGuid();
        await InsertMatchAsync(
            Guid.NewGuid(), Guid.NewGuid(), "CONFIRMED", foundItemId: sharedFoundItemId);

        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED", foundItemId: sharedFoundItemId);

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.DecideAsync(matchId, finderId, confirm: true,
                "finder@example.com", "+94771234567", CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    // Confirming one match for a report deactivates any other still-pending match for the same report,
    // since only one match per report can ever reach Confirmed.
    [Fact]
    public async Task DecideAsync_Confirm_DeactivatesOtherPendingMatchesForTheSameItems()
    {
        var sharedFoundItemId = Guid.NewGuid();
        var (otherMatchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), Guid.NewGuid(), "FINDER_CONFIRMED", foundItemId: sharedFoundItemId);

        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), finderId, "LOST_REPORTER_CONFIRMED", foundItemId: sharedFoundItemId);

        await _repository.DecideAsync(matchId, finderId, confirm: true,
            "finder@example.com", "+94771234567", CancellationToken.None);

        var (_, otherIsActive) = await ReadMatchStateAsync(otherMatchId);
        Assert.False(otherIsActive);
    }

    // Scenario 6: once Confirmed, the finder can read back the lost reporter's stored contact details.
    [Fact]
    public async Task GetLostReporterContactAsync_MatchConfirmedWithContactStored_ReturnsEmailAndPhone()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), finderId, "CONFIRMED",
            lostReporterEmail: "lostreporter@example.com", lostReporterPhone: "+94770000002");

        var contact = await _repository.GetLostReporterContactAsync(
            matchId, finderId, CancellationToken.None);

        Assert.Equal("lostreporter@example.com", contact.Email);
        Assert.Equal("+94770000002", contact.Phone);
    }

    [Fact]
    public async Task GetLostReporterContactAsync_CallerIsNotFinder_ThrowsForbidden()
    {
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), Guid.NewGuid(), "CONFIRMED",
            lostReporterEmail: "lostreporter@example.com", lostReporterPhone: "+94770000002");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.GetLostReporterContactAsync(matchId, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
    }

    [Theory]
    [InlineData("LOST_REPORTER_CONFIRMED", true)]
    [InlineData("CONFIRMED", false)]
    public async Task GetLostReporterContactAsync_NotConfirmedOrInactive_ThrowsConflict(string status, bool isActive)
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(
            Guid.NewGuid(), finderId, status, isActive,
            lostReporterEmail: "lostreporter@example.com", lostReporterPhone: "+94770000002");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.GetLostReporterContactAsync(matchId, finderId, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    // A pre-outbox or otherwise-legacy Confirmed row with no stored contact fails clearly rather than returning blanks.
    [Fact]
    public async Task GetLostReporterContactAsync_ContactNotStored_ThrowsServiceUnavailable()
    {
        var finderId = Guid.NewGuid();
        var (matchId, _, _) = await InsertMatchAsync(Guid.NewGuid(), finderId, "CONFIRMED");

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.GetLostReporterContactAsync(matchId, finderId, CancellationToken.None));

        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task GetLostReporterContactAsync_MatchNotFound_ThrowsNotFound()
    {
        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            _repository.GetLostReporterContactAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
    }
}
