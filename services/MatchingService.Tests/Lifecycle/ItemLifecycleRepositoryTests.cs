using MatchingService.Claims;
using MatchingService.Lifecycle;
using MatchingService.Tests.Integration;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Lifecycle;

/// <summary>
/// Story 7 (item lifecycle to match deactivation). Real, disposable MySQL (Testcontainers, via
/// ClaimServiceDbFixture, running migration 010_AddItemLifecycle.sql) proving ItemLifecycleRepository's
/// own SQL: Scenario 1/2 (resolve/delete deactivates active matches), Scenario 3 (a Confirmed match is
/// protected), Scenario 4/6 (deactivated matches carry an audit trail and are excluded going forward -
/// their pending notifications are cancelled, not silently left to retry forever), Scenario 8-style
/// duplicate-event prevention, and EnsurePairActiveAsync (the claim-creation-time guard ClaimRepository
/// relies on to block a claim against an already-resolved/deleted report).
/// </summary>
public sealed class ItemLifecycleRepositoryTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public ItemLifecycleRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    // ---- Seeding helpers -----------------------------------------------------------

    private async Task<Guid> InsertMatchAsync(
        string status,
        Guid lostItemId,
        Guid foundItemId,
        bool isActive = true)
    {
        var id = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@lostReporterId", Guid.NewGuid());
        command.Parameters.AddWithValue("@finderId", Guid.NewGuid());
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@snapshot", snapshot);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<Guid> InsertNotificationAsync(Guid matchId, string status)
    {
        var id = Guid.NewGuid();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO match_notifications (
                id, match_id, recipient_user_id, notification_type, status, attempts,
                next_attempt_at, created_at, updated_at
            ) VALUES (
                @id, @matchId, @recipientUserId, 'MATCH_CONFIRMED', @status, 0,
                UTC_TIMESTAMP(3), UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@recipientUserId", Guid.NewGuid());
        command.Parameters.AddWithValue("@status", status);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<(bool IsActive, string? DeactivationReason, Guid? DeactivatedItemId, string? DeactivatedItemType)>
        ReadMatchAsync(Guid matchId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT is_active, deactivation_reason, deactivated_item_id, deactivated_item_type
            FROM matches WHERE id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task<string> ReadNotificationStatusAsync(Guid notificationId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status FROM match_notifications WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", notificationId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadInactiveReasonAsync(string itemType, Guid itemId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT inactive_reason FROM matching_item_states
            WHERE item_type = @type AND item_id = @itemId;
            """, connection);
        command.Parameters.AddWithValue("@type", itemType);
        command.Parameters.AddWithValue("@itemId", itemId);

        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    // ---- ApplyAsync: Scenarios 1/2 (resolve/delete deactivates active matches) ---------

    [Theory]
    [InlineData("AWAITING_CLAIMANT_CONFIRMATION")]
    [InlineData("LOST_REPORTER_CONFIRMED")]
    [InlineData("FINDER_CONFIRMED")]
    public async Task ApplyAsync_ResolvedLostItem_DeactivatesActiveMatchWithReasonAndAuditFields(string status)
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(status, lostItemId, foundItemId);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var evt = new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow);
        await repository.ApplyAsync(evt, CancellationToken.None);

        var (isActive, reason, deactivatedItemId, deactivatedItemType) = await ReadMatchAsync(matchId);
        Assert.False(isActive);
        Assert.Equal("ITEM_RESOLVED", reason);
        Assert.Equal(lostItemId, deactivatedItemId);
        Assert.Equal("LOST", deactivatedItemType);
    }

    [Fact]
    public async Task ApplyAsync_DeletedFoundItem_DeactivatesActiveMatchWithItemDeletedReason()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", lostItemId, foundItemId);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var evt = new ItemLifecycleEvent(Guid.NewGuid(), foundItemId, "FOUND", "DELETED", DateTime.UtcNow);
        await repository.ApplyAsync(evt, CancellationToken.None);

        var (isActive, reason, deactivatedItemId, deactivatedItemType) = await ReadMatchAsync(matchId);
        Assert.False(isActive);
        Assert.Equal("ITEM_DELETED", reason);
        Assert.Equal(foundItemId, deactivatedItemId);
        Assert.Equal("FOUND", deactivatedItemType);
    }

    // ---- Scenario 3: a Confirmed match is protected -------------------------------------

    [Fact]
    public async Task ApplyAsync_ResolvedItem_LeavesAConfirmedMatchCompletelyUntouched()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("CONFIRMED", lostItemId, foundItemId);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var evt = new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow);
        await repository.ApplyAsync(evt, CancellationToken.None);

        var (isActive, reason, _, _) = await ReadMatchAsync(matchId);
        Assert.True(isActive);
        Assert.Null(reason);
    }

    [Fact]
    public async Task ApplyAsync_ResolvedItem_LeavesAnAlreadyRejectedMatchUntouched()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("REJECTED", lostItemId, foundItemId);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var evt = new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow);
        await repository.ApplyAsync(evt, CancellationToken.None);

        var (isActive, reason, _, _) = await ReadMatchAsync(matchId);
        Assert.True(isActive);
        Assert.Null(reason);
    }

    [Fact]
    public async Task ApplyAsync_AlreadyInactiveMatch_IsNotTouchedAgain()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", lostItemId, foundItemId, isActive: false);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var evt = new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow);
        await repository.ApplyAsync(evt, CancellationToken.None);

        var (isActive, reason, _, _) = await ReadMatchAsync(matchId);
        Assert.False(isActive);
        Assert.Null(reason); // never touched, so still whatever it was before (nothing in this case)
    }

    // ---- Terminal-reason priority: delete always wins over resolve, either order ------

    [Fact]
    public async Task ApplyAsync_ResolveAfterDelete_TheItemStateStaysDeleted()
    {
        var itemId = Guid.NewGuid();
        var repository = new ItemLifecycleRepository(_fixture.Connections);

        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), itemId, "LOST", "DELETED", DateTime.UtcNow),
            CancellationToken.None);
        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), itemId, "LOST", "RESOLVED", DateTime.UtcNow),
            CancellationToken.None);

        Assert.Equal("DELETED", await ReadInactiveReasonAsync("LOST", itemId));
    }

    [Fact]
    public async Task ApplyAsync_DeleteAfterResolve_UpgradesTheItemStateToDeleted()
    {
        var itemId = Guid.NewGuid();
        var repository = new ItemLifecycleRepository(_fixture.Connections);

        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), itemId, "LOST", "RESOLVED", DateTime.UtcNow),
            CancellationToken.None);
        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), itemId, "LOST", "DELETED", DateTime.UtcNow),
            CancellationToken.None);

        Assert.Equal("DELETED", await ReadInactiveReasonAsync("LOST", itemId));
    }

    // ---- Duplicate-event prevention -----------------------------------------------------

    [Fact]
    public async Task ApplyAsync_SameEventIdRedelivered_IsANoOpTheSecondTime()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", lostItemId, foundItemId);

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        var eventId = Guid.NewGuid();
        var evt = new ItemLifecycleEvent(eventId, lostItemId, "LOST", "RESOLVED", DateTime.UtcNow);

        await repository.ApplyAsync(evt, CancellationToken.None);
        var (_, reasonAfterFirst, _, _) = await ReadMatchAsync(matchId);

        // Redeliver the identical event - must not throw, and must not somehow "double apply".
        await repository.ApplyAsync(evt, CancellationToken.None);
        var (isActive, reasonAfterSecond, _, _) = await ReadMatchAsync(matchId);

        Assert.False(isActive);
        Assert.Equal(reasonAfterFirst, reasonAfterSecond);
    }

    // ---- Notifications: cancelled for a now-inactive match, SENT ones left as history --

    [Fact]
    public async Task ApplyAsync_DeactivatesAMatch_CancelsItsPendingAndFailedNotifications()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", lostItemId, foundItemId);

        var pendingId = await InsertNotificationAsync(matchId, "PENDING");
        var failedId = await InsertNotificationAsync(matchId, "FAILED");
        var sentId = await InsertNotificationAsync(matchId, "SENT");

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow),
            CancellationToken.None);

        Assert.Equal("CANCELLED", await ReadNotificationStatusAsync(pendingId));
        Assert.Equal("CANCELLED", await ReadNotificationStatusAsync(failedId));
        Assert.Equal("SENT", await ReadNotificationStatusAsync(sentId)); // delivery history is preserved
    }

    // ---- EnsurePairActiveAsync: the claim-creation-time guard --------------------------

    [Fact]
    public async Task EnsurePairActiveAsync_BothItemsActive_SucceedsWithoutThrowing()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ItemLifecycleRepository.EnsurePairActiveAsync(
            connection, transaction, lostItemId, foundItemId, CancellationToken.None);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task EnsurePairActiveAsync_LostItemAlreadyResolved_ThrowsConflict()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), lostItemId, "LOST", "RESOLVED", DateTime.UtcNow),
            CancellationToken.None);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            ItemLifecycleRepository.EnsurePairActiveAsync(
                connection, transaction, lostItemId, foundItemId, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task EnsurePairActiveAsync_FoundItemAlreadyDeleted_ThrowsConflict()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();

        var repository = new ItemLifecycleRepository(_fixture.Connections);
        await repository.ApplyAsync(
            new ItemLifecycleEvent(Guid.NewGuid(), foundItemId, "FOUND", "DELETED", DateTime.UtcNow),
            CancellationToken.None);

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var exception = await Assert.ThrowsAsync<ClaimException>(() =>
            ItemLifecycleRepository.EnsurePairActiveAsync(
                connection, transaction, lostItemId, foundItemId, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }
}
