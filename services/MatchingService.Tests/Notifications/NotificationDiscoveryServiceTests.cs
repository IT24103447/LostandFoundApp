using MatchingService.Notifications;
using MatchingService.Tests.Integration;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>
/// Story 6 (email notifications). Real, disposable MySQL (Testcontainers, via ClaimServiceDbFixture)
/// proving NotificationDiscoveryService's polling queries: Scenario 2 (counterpart notified once the
/// claimant confirms), Scenarios 3/4 (both parties notified on Confirmed/Rejected), Scenario 5 (no
/// notification for an auto-rejected claim), Scenario 8's insertion half of duplicate prevention (the
/// UNIQUE constraint plus INSERT IGNORE meaning re-running discovery never creates a second row for the
/// same match/recipient/type), and the stuck-in-flight cleanup that stops indefinite retries after a
/// crashed worker used its final attempt without saving a result.
///
/// match_notification_activation's activated_at is seeded to a point in the past by migration
/// 009_CreateMatchNotifications.sql itself (INSERT ... VALUES (1, UTC_TIMESTAMP(3)) at migration time,
/// which ran before any of these tests' own matches/match_actions rows are created), so every row these
/// tests insert is already "after activation" without needing to touch that table directly.
/// </summary>
public sealed class NotificationDiscoveryServiceTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public NotificationDiscoveryServiceTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> InsertMatchAsync(
        string status,
        string claimantRole,
        bool isActive = true,
        Guid? lostReporterId = null,
        Guid? finderId = null)
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
                @claimantRole, @status, @isActive, 75.00, 'text-v1',
                @snapshot, @snapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@foundItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@finderId", finderId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@claimantRole", claimantRole);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@snapshot", snapshot);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertMatchActionAsync(
        Guid matchId, Guid actorUserId, string actorRole, string action, string previousStatus, string newStatus)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO match_actions (
                id, match_id, actor_user_id, actor_role, action,
                previous_status, new_status, created_at
            ) VALUES (
                @id, @matchId, @actorUserId, @actorRole, @action,
                @previousStatus, @newStatus, UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@actorUserId", actorUserId);
        command.Parameters.AddWithValue("@actorRole", actorRole);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@previousStatus", previousStatus);
        command.Parameters.AddWithValue("@newStatus", newStatus);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(Guid MatchId, Guid RecipientUserId, string Type, string Status)>> ReadNotificationsAsync(
        Guid matchId)
    {
        var results = new List<(Guid, Guid, string, string)>();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT match_id, recipient_user_id, notification_type, status FROM match_notifications WHERE match_id = @matchId;",
            connection);
        command.Parameters.AddWithValue("@matchId", matchId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
        }

        return results;
    }

    // ---- Scenario 2: counterpart notified once the claimant confirms -------------------

    [Theory]
    [InlineData("LOST", "LOST_REPORTER_CONFIRMED", true)] // recipient should be the finder
    [InlineData("FOUND", "FINDER_CONFIRMED", false)] // recipient should be the lost reporter
    public async Task DiscoverAsync_HalfConfirmedMatch_QueuesCounterpartActionForTheOtherParty(
        string claimantRole, string status, bool recipientShouldBeFinder)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            status, claimantRole, lostReporterId: lostReporterId, finderId: finderId);

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        var rows = await ReadNotificationsAsync(matchId);
        var row = Assert.Single(rows);

        Assert.Equal(NotificationTypes.CounterpartAction, row.Type);
        Assert.Equal(recipientShouldBeFinder ? finderId : lostReporterId, row.RecipientUserId);
        Assert.Equal("PENDING", row.Status);
    }

    [Fact]
    public async Task DiscoverAsync_MatchNotYetHalfConfirmed_QueuesNothing()
    {
        var matchId = await InsertMatchAsync("AWAITING_CLAIMANT_CONFIRMATION", "LOST");

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Empty(await ReadNotificationsAsync(matchId));
    }

    [Fact]
    public async Task DiscoverAsync_RunTwice_NeverCreatesADuplicateCounterpartNotification()
    {
        // Scenario 8 (dedup), the discovery half: the UNIQUE (match_id, recipient_user_id,
        // notification_type) constraint plus INSERT IGNORE means re-discovering an
        // already-queued transition is a no-op, not a second row.
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "LOST");

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);
        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Single(await ReadNotificationsAsync(matchId));
    }

    // ---- Scenarios 3/4: both parties notified on Confirmed/Rejected --------------------

    [Theory]
    [InlineData("CONFIRMED", "CONFIRM", NotificationTypes.MatchConfirmed)]
    [InlineData("REJECTED", "REJECT", NotificationTypes.MatchRejected)]
    public async Task DiscoverAsync_TerminalMatchAction_QueuesBothPartiesTheCorrectOutcomeType(
        string newStatus, string action, string expectedType)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            newStatus, "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertMatchActionAsync(
            matchId, finderId, "FOUND", action, "LOST_REPORTER_CONFIRMED", newStatus);

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        var rows = await ReadNotificationsAsync(matchId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(expectedType, row.Type));
        Assert.Contains(rows, row => row.RecipientUserId == lostReporterId);
        Assert.Contains(rows, row => row.RecipientUserId == finderId);
    }

    [Fact]
    public async Task DiscoverAsync_MatchActionButMatchNoLongerAtThatStatus_QueuesNothing()
    {
        // The match_actions row is historical; if the match's own current status has since
        // moved on again, the outcome query's own "m.status = a.new_status" guard must skip it.
        var matchId = await InsertMatchAsync("REJECTED", "LOST");
        await InsertMatchActionAsync(
            matchId, Guid.NewGuid(), "FOUND", "CONFIRM", "LOST_REPORTER_CONFIRMED", "CONFIRMED");

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Empty(await ReadNotificationsAsync(matchId));
    }

    [Fact]
    public async Task DiscoverAsync_InactiveMatch_QueuesNothingForTheOutcome()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST", isActive: false);
        await InsertMatchActionAsync(
            matchId, Guid.NewGuid(), "FOUND", "CONFIRM", "LOST_REPORTER_CONFIRMED", "CONFIRMED");

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Empty(await ReadNotificationsAsync(matchId));
    }

    // ---- Scenario 5: no notification for an auto-rejected claim ------------------------

    [Fact]
    public async Task DiscoverAsync_AutoRejectedLowConfidenceMatch_QueuesNothingAtAll()
    {
        var matchId = await InsertMatchAsync("AUTO_REJECTED_LOW_CONFIDENCE", "LOST");

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Empty(await ReadNotificationsAsync(matchId));
    }

    // ---- Stuck in-flight cleanup ---------------------------------------------------------

    [Fact]
    public async Task DiscoverAsync_ExhaustedInFlightRow_IsMarkedFailedWithDeliveryStateUnknown()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");

        Guid notificationId;
        await using (var connection = _fixture.Connections.Create())
        {
            await connection.OpenAsync();
            notificationId = Guid.NewGuid();

            await using var command = new MySqlCommand("""
                INSERT INTO match_notifications (
                    id, match_id, recipient_user_id, notification_type, status, attempts,
                    next_attempt_at, lease_token, lease_expires_at, created_at, updated_at
                ) VALUES (
                    @id, @matchId, @recipientUserId, @type, 'PENDING', 5,
                    UTC_TIMESTAMP(3), @leaseToken, @leaseExpiresAt, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
                );
                """, connection);
            command.Parameters.AddWithValue("@id", notificationId);
            command.Parameters.AddWithValue("@matchId", matchId);
            command.Parameters.AddWithValue("@recipientUserId", Guid.NewGuid());
            command.Parameters.AddWithValue("@type", NotificationTypes.MatchConfirmed);
            // A crashed worker: leased, but the lease has since expired without a result saved.
            command.Parameters.AddWithValue("@leaseToken", Guid.NewGuid());
            command.Parameters.AddWithValue("@leaseExpiresAt", DateTime.UtcNow.AddMinutes(-5));

            await command.ExecuteNonQueryAsync();
        }

        var discovery = new NotificationDiscoveryService(_fixture.Connections);
        await discovery.DiscoverAsync(CancellationToken.None);

        await using var readConnection = _fixture.Connections.Create();
        await readConnection.OpenAsync();
        await using var readCommand = new MySqlCommand(
            "SELECT status, error_code, lease_token FROM match_notifications WHERE id = @id;",
            readConnection);
        readCommand.Parameters.AddWithValue("@id", notificationId);

        await using var reader = await readCommand.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal("FAILED", reader.GetString(0));
        Assert.Equal("DELIVERY_STATE_UNKNOWN", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
    }
}
