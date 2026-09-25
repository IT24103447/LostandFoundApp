using MatchingService.Notifications;
using MatchingService.Tests.Integration;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>
/// Story 6 (email notifications). Real, disposable MySQL (Testcontainers, via ClaimServiceDbFixture,
/// running the real migrations including 009_CreateMatchNotifications.sql) proving NotificationRepository's
/// own SQL: claim/lease semantics, recipient-relevance rules per notification type, and the Sent/Failed/
/// Cancelled status transitions (Scenarios 6, 7, and the claim-exclusivity half of Scenario 8's dedup).
/// No IMatchEmailSender/SMTP involved anywhere here - this is the repository layer only.
/// </summary>
public sealed class NotificationRepositoryTests : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _fixture;

    public NotificationRepositoryTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    // ClaimServiceDbFixture's MySQL container is shared across every test method in this class
    // (xUnit's IClassFixture semantics) and test methods run sequentially, not isolated per method -
    // so a due row a previous test left unclaimed would otherwise get picked up by a later test's own
    // plain ClaimNextAsync() call before that test's own freshly-inserted row is reached (ClaimNextAsync
    // always claims the globally oldest due row, with no per-test scoping). Draining any such leftovers
    // before every test method runs (xUnit constructs a fresh instance of this class per test method,
    // so IAsyncLifetime.InitializeAsync runs once per test) resets the "due queue" to empty each time.
    public async Task InitializeAsync()
    {
        var repository = new NotificationRepository(_fixture.Connections);
        while (await repository.ClaimNextAsync(CancellationToken.None) is not null)
        {
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Seeding helpers -----------------------------------------------------------

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

    private async Task<Guid> InsertNotificationAsync(
        Guid matchId,
        Guid recipientUserId,
        string type,
        string status = "PENDING",
        int attempts = 0,
        DateTime? nextAttemptAt = null,
        Guid? leaseToken = null,
        DateTime? leaseExpiresAt = null)
    {
        var id = Guid.NewGuid();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO match_notifications (
                id, match_id, recipient_user_id, notification_type, status, attempts,
                next_attempt_at, lease_token, lease_expires_at, created_at, updated_at
            ) VALUES (
                @id, @matchId, @recipientUserId, @type, @status, @attempts,
                @nextAttemptAt, @leaseToken, @leaseExpiresAt, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@recipientUserId", recipientUserId);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@nextAttemptAt", (object?)nextAttemptAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@leaseToken", (object?)leaseToken ?? DBNull.Value);
        command.Parameters.AddWithValue("@leaseExpiresAt", (object?)leaseExpiresAt ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task InsertContactAsync(Guid userId, string email, bool isActive = true, bool isDeleted = false)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO notification_contacts (
                user_id, email, is_active, is_deleted, last_event_at, updated_at
            ) VALUES (
                @userId, @email, @isActive, @isDeleted, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@isDeleted", isDeleted);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<(string Status, int Attempts, DateTime? NextAttemptAt, Guid? LeaseToken)> ReadNotificationAsync(
        Guid id)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, attempts, next_attempt_at, lease_token FROM match_notifications WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    // ---- ClaimNextAsync --------------------------------------------------------------

    [Fact]
    public async Task ClaimNextAsync_NoDueRows_ReturnsNull()
    {
        var repository = new NotificationRepository(_fixture.Connections);

        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);
    }

    [Fact]
    public async Task ClaimNextAsync_DuePendingRow_ClaimsItAndIncrementsAttempts()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        var recipientId = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, recipientId, NotificationTypes.CounterpartAction,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job!.Id);
        Assert.Equal(matchId, job.MatchId);
        Assert.Equal(recipientId, job.RecipientUserId);
        Assert.Equal(NotificationTypes.CounterpartAction, job.Type);
        Assert.Equal(1, job.Attempts);

        var (status, attempts, _, leaseToken) = await ReadNotificationAsync(notificationId);
        Assert.Equal("PENDING", status);
        Assert.Equal(1, attempts);
        Assert.Equal(job.LeaseToken, leaseToken);
    }

    [Fact]
    public async Task ClaimNextAsync_RowNotYetDue_IsNotClaimed()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            nextAttemptAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);
    }

    [Fact]
    public async Task ClaimNextAsync_RowAlreadyLeasedByAnotherWorker_IsNotClaimed()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5),
            leaseToken: Guid.NewGuid(),
            leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);
    }

    [Fact]
    public async Task ClaimNextAsync_RowWithExpiredLease_IsReclaimable()
    {
        // Simulates a worker that crashed after leasing but before delivering - the lease
        // expiring must let a later worker pick the same row back up.
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5),
            leaseToken: Guid.NewGuid(),
            leaseExpiresAt: DateTime.UtcNow.AddMinutes(-1));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job!.Id);
    }

    [Fact]
    public async Task ClaimNextAsync_AttemptsAtMax_IsNeverClaimedAgain()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            status: "FAILED", attempts: 5,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.Null(job);
    }

    [Fact]
    public async Task ClaimNextAsync_MultipleDueRows_ClaimsOldestFirst()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");

        var olderId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5));
        await Task.Delay(1500); // ensure a genuinely later created_at (DATETIME(3), millisecond precision)
        await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-5));

        var repository = new NotificationRepository(_fixture.Connections);
        var first = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(olderId, first!.Id);
    }

    [Fact]
    public async Task ClaimNextAsync_FailedRowPastBackoff_IsReclaimable()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction,
            status: "FAILED", attempts: 1,
            nextAttemptAt: DateTime.UtcNow.AddSeconds(-1));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = await repository.ClaimNextAsync(CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(notificationId, job!.Id);
        Assert.Equal(2, job.Attempts);
    }

    // ---- GetRecipientAsync -------------------------------------------------------------

    [Fact]
    public async Task GetRecipientAsync_LeaseLost_ReturnsNotificationLeaseLost()
    {
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", "FOUND");
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction);

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.CounterpartAction, 1, Guid.NewGuid());

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.False(recipient.CanSend);
        Assert.Equal("NOTIFICATION_LEASE_LOST", recipient.SuppressionCode);
    }

    [Fact]
    public async Task GetRecipientAsync_MatchInactive_ReturnsMatchInactive()
    {
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "LOST_REPORTER_CONFIRMED", "FOUND", isActive: false, finderId: finderId);
        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, finderId, NotificationTypes.CounterpartAction,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.CounterpartAction, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.False(recipient.CanSend);
        Assert.Equal("MATCH_INACTIVE", recipient.SuppressionCode);
    }

    [Theory]
    [InlineData("LOST", "LOST_REPORTER_CONFIRMED", true)] // recipient = finder
    [InlineData("FOUND", "FINDER_CONFIRMED", false)] // recipient = lost reporter
    public async Task GetRecipientAsync_CounterpartAction_RelevantWhenClaimantRoleAndStatusAgree(
        string claimantRole, string status, bool recipientIsFinder)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var recipientId = recipientIsFinder ? finderId : lostReporterId;

        var matchId = await InsertMatchAsync(
            status, claimantRole, lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(recipientId, "recipient@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, recipientId, NotificationTypes.CounterpartAction,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, recipientId, NotificationTypes.CounterpartAction, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.True(recipient.CanSend);
        Assert.Equal("recipient@example.com", recipient.Email);
    }

    [Fact]
    public async Task GetRecipientAsync_CounterpartActionNoLongerAtThatStatus_ReturnsNoLongerRelevant()
    {
        // The match moved on (e.g. already CONFIRMED) by the time the worker got to this job -
        // the counterpart-action reminder is stale and must not be sent.
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, finderId, NotificationTypes.CounterpartAction,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.CounterpartAction, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.False(recipient.CanSend);
        Assert.Equal("NOTIFICATION_NO_LONGER_RELEVANT", recipient.SuppressionCode);
    }

    [Theory]
    [InlineData(NotificationTypes.MatchConfirmed, "CONFIRMED")]
    [InlineData(NotificationTypes.MatchRejected, "REJECTED")]
    public async Task GetRecipientAsync_OutcomeType_RelevantForEitherParticipant(string type, string status)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            status, "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, finderId, type,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(notificationId, matchId, finderId, type, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.True(recipient.CanSend);
        Assert.Equal("finder@example.com", recipient.Email);
    }

    [Fact]
    public async Task GetRecipientAsync_OutcomeTypeRecipientNotAParticipant_ReturnsNoLongerRelevant()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var stranger = Guid.NewGuid();

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, stranger, NotificationTypes.MatchConfirmed,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, stranger, NotificationTypes.MatchConfirmed, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.False(recipient.CanSend);
        Assert.Equal("NOTIFICATION_NO_LONGER_RELEVANT", recipient.SuppressionCode);
    }

    [Fact]
    public async Task GetRecipientAsync_NoContactRowYet_ReturnsCanSendTrueWithNullEmail()
    {
        // No notification_contacts row synced yet - GetRecipientAsync itself does not suppress
        // this; NotificationDeliveryService is the one that turns a null email into a failure.
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.True(recipient.CanSend);
        Assert.Null(recipient.Email);
    }

    [Theory]
    [InlineData(false, false)] // is_active = false
    [InlineData(true, true)] // is_deleted = true
    public async Task GetRecipientAsync_ContactInactiveOrDeleted_ReturnsRecipientInactive(
        bool isActive, bool isDeleted)
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com", isActive: isActive, isDeleted: isDeleted);

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);

        var recipient = await repository.GetRecipientAsync(job, CancellationToken.None);

        Assert.False(recipient.CanSend);
        Assert.Equal("RECIPIENT_INACTIVE", recipient.SuppressionCode);
    }

    // ---- MarkSentAsync / MarkCancelledAsync / MarkFailedAsync -------------------------

    [Fact]
    public async Task MarkSentAsync_LeasedPendingRow_SetsSentAndClearsLease()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed, 1, leaseToken);

        var recorded = await repository.MarkSentAsync(job, CancellationToken.None);

        Assert.True(recorded);
        var (status, _, nextAttemptAt, storedLease) = await ReadNotificationAsync(notificationId);
        Assert.Equal("SENT", status);
        Assert.Null(nextAttemptAt);
        Assert.Null(storedLease);
    }

    [Fact]
    public async Task MarkSentAsync_WrongLeaseToken_DoesNothingAndReturnsFalse()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var realLease = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            leaseToken: realLease, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed, 1, Guid.NewGuid());

        var recorded = await repository.MarkSentAsync(job, CancellationToken.None);

        Assert.False(recorded);
        var (status, _, _, _) = await ReadNotificationAsync(notificationId);
        Assert.Equal("PENDING", status);
    }

    [Fact]
    public async Task MarkCancelledAsync_SetsCancelledWithSuppressionCode()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed, 1, leaseToken);

        var recorded = await repository.MarkCancelledAsync(job, "MATCH_INACTIVE", CancellationToken.None);

        Assert.True(recorded);
        var (status, _, nextAttemptAt, storedLease) = await ReadNotificationAsync(notificationId);
        Assert.Equal("CANCELLED", status);
        Assert.Null(nextAttemptAt);
        Assert.Null(storedLease);
    }

    [Fact]
    public async Task MarkFailedAsync_BelowMaxAttempts_SchedulesRetryWithBackoff()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            attempts: 2, leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed, 2, leaseToken);

        var recorded = await repository.MarkFailedAsync(job, "SMTP_ERROR_421", CancellationToken.None);

        Assert.True(recorded);
        var (status, _, nextAttemptAt, storedLease) = await ReadNotificationAsync(notificationId);
        Assert.Equal("FAILED", status);
        Assert.NotNull(nextAttemptAt);
        Assert.True(nextAttemptAt > DateTime.UtcNow);
        Assert.Null(storedLease);
    }

    [Fact]
    public async Task MarkFailedAsync_AtMaxAttempts_LeavesNoFurtherRetryScheduled()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST");
        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertNotificationAsync(
            matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed,
            attempts: 5, leaseToken: leaseToken, leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        var repository = new NotificationRepository(_fixture.Connections);
        var job = new NotificationJob(
            notificationId, matchId, Guid.NewGuid(), NotificationTypes.MatchConfirmed, 5, leaseToken);

        await repository.MarkFailedAsync(job, "SMTP_ERROR_421", CancellationToken.None);

        var (status, _, nextAttemptAt, _) = await ReadNotificationAsync(notificationId);
        Assert.Equal("FAILED", status);
        Assert.Null(nextAttemptAt);
    }
}
