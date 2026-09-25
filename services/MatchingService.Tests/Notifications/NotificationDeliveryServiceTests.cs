using System.Net.Mail;
using MatchingService.Databases;
using MatchingService.Notifications;
using MatchingService.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>
/// Story 6 (email notifications). Real MySQL (Testcontainers, via ClaimServiceDbFixture) and the real
/// NotificationRepository - only IMatchEmailSender is swapped for an in-memory fake, the same "fake at
/// the SMTP boundary, real everywhere else" design agreed with the user: this is what runs in CI, where
/// no real SMTP credentials exist, and it still genuinely exercises recipient-relevance resolution,
/// status recording, and retry-backoff math against real SQL - only the literal network send is faked.
/// The real MatchEmailSender itself is proven separately, locally, by
/// MatchNotificationMailtrapIntegrationTests, which self-skips without real Mailtrap credentials.
/// </summary>
public sealed class NotificationDeliveryServiceTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;

    public NotificationDeliveryServiceTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Mirrors AuthService.Tests' own FakeEmailService: records what would have been sent,
    /// sends nothing for real. Can be told to throw instead, to drive DeliverAsync's failure branches,
    /// or to delay (respecting the real cancellationToken) to drive its cancellation-mid-flight branch.</summary>
    private sealed class FakeMatchEmailSender : IMatchEmailSender
    {
        public List<(NotificationJob Job, string RecipientEmail)> Sent { get; } = new();
        public Exception? ThrowOnSend { get; set; }
        public TimeSpan? DelayBeforeSend { get; set; }

        public async Task SendAsync(NotificationJob job, string recipientEmail, CancellationToken cancellationToken)
        {
            if (DelayBeforeSend is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            Sent.Add((job, recipientEmail));
        }
    }

    /// <summary>Wraps the real connection factory but throws on the Nth call to Create() onward - used
    /// to force MarkSentAsync's own database write to fail AFTER a real send already succeeded, the
    /// "SMTP accepted but DB save failed" branch that can't be reached any other way (it deliberately
    /// isn't retried, since SMTP acceptance can't be rolled back).</summary>
    private sealed class FailFromNthCallConnectionFactory(IDbConnectionFactory inner, int failFromCallNumber)
        : IDbConnectionFactory
    {
        private int _callCount;

        public MySqlConnection Create()
        {
            _callCount++;
            if (_callCount >= failFromCallNumber)
            {
                throw new InvalidOperationException(
                    "Simulated DB failure after a successful send, for MarkSentAsync-fails resilience test.");
            }

            return inner.Create();
        }
    }

    private async Task<Guid> InsertMatchAsync(
        string status, string claimantRole, bool isActive = true,
        Guid? lostReporterId = null, Guid? finderId = null)
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

    private async Task<Guid> InsertLeasedNotificationAsync(
        Guid matchId, Guid recipientId, string type, Guid leaseToken, int attempts = 1)
    {
        var id = Guid.NewGuid();

        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO match_notifications (
                id, match_id, recipient_user_id, notification_type, status, attempts,
                next_attempt_at, lease_token, lease_expires_at, created_at, updated_at
            ) VALUES (
                @id, @matchId, @recipientId, @type, 'PENDING', @attempts,
                UTC_TIMESTAMP(3), @leaseToken, DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 5 MINUTE),
                UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@matchId", matchId);
        command.Parameters.AddWithValue("@recipientId", recipientId);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@leaseToken", leaseToken);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<string> ReadStatusAsync(Guid notificationId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status FROM match_notifications WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", notificationId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadErrorCodeAsync(Guid notificationId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT error_code FROM match_notifications WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", notificationId);

        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    [Fact]
    public async Task DeliverAsync_RelevantRecipientWithContact_SendsAndMarksSent()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("finder@example.com", sent.RecipientEmail);
        Assert.Equal("SENT", await ReadStatusAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_MatchNoLongerActive_CancelsWithoutCallingTheSender()
    {
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST", isActive: false);
        var recipientId = Guid.NewGuid();

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, recipientId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, recipientId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Empty(sender.Sent);
        Assert.Equal("CANCELLED", await ReadStatusAsync(notificationId));
        Assert.Equal("MATCH_INACTIVE", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_NoContactRowYet_FailsWithRecipientEmailUnavailable_WithoutCallingTheSender()
    {
        var recipientId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("CONFIRMED", "LOST", finderId: recipientId);

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, recipientId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, recipientId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Empty(sender.Sent);
        Assert.Equal("FAILED", await ReadStatusAsync(notificationId));
        Assert.Equal("RECIPIENT_EMAIL_UNAVAILABLE", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_SenderThrowsNotificationDeliveryException_RecordsItsOwnCode()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender
        {
            ThrowOnSend = new NotificationDeliveryException("SMTP_TIMEOUT")
        };
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Equal("FAILED", await ReadStatusAsync(notificationId));
        Assert.Equal("SMTP_TIMEOUT", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_SenderThrowsSmtpException_RecordsSmtpErrorWithStatusCode()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender
        {
            ThrowOnSend = new SmtpException(SmtpStatusCode.MailboxBusy)
        };
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Equal("FAILED", await ReadStatusAsync(notificationId));
        Assert.Equal($"SMTP_ERROR_{(int)SmtpStatusCode.MailboxBusy}", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_SenderThrowsUnexpectedException_RecordsGenericSendFailedCode()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender { ThrowOnSend = new InvalidOperationException("boom") };
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Equal("FAILED", await ReadStatusAsync(notificationId));
        Assert.Equal("NOTIFICATION_SEND_FAILED", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_RecipientInactive_CancelsWithoutCallingTheSender()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com", isActive: false);

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Empty(sender.Sent);
        Assert.Equal("CANCELLED", await ReadStatusAsync(notificationId));
        Assert.Equal("RECIPIENT_INACTIVE", await ReadErrorCodeAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_CancelledWhileSending_PropagatesTheCancellationRatherThanSwallowingIt()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        var repository = new NotificationRepository(_fixture.Connections);
        var sender = new FakeMatchEmailSender { DelayBeforeSend = TimeSpan.FromSeconds(5) };
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);

        using var cts = new CancellationTokenSource();
        var deliverTask = delivery.DeliverAsync(job, cts.Token);

        // Cancel while the fake sender is still mid-"send" (its own 5s delay), not before or after -
        // this is what actually drives DeliverAsync's own cancellation-mid-flight rethrow branch,
        // rather than the trivial case of an already-cancelled token.
        await Task.Delay(300);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deliverTask);

        // Cancellation is not a delivery outcome - it must not be recorded as SENT/FAILED/CANCELLED,
        // since the caller (the worker loop) is the one who decides what a shutdown mid-delivery means.
        Assert.Equal("PENDING", await ReadStatusAsync(notificationId));
    }

    [Fact]
    public async Task DeliverAsync_SendSucceedsButMarkSentFails_LogsWithoutMisrecordingTheOutcome()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertMatchAsync(
            "CONFIRMED", "LOST", lostReporterId: lostReporterId, finderId: finderId);
        await InsertContactAsync(finderId, "finder@example.com");

        var leaseToken = Guid.NewGuid();
        var notificationId = await InsertLeasedNotificationAsync(
            matchId, finderId, NotificationTypes.MatchConfirmed, leaseToken);

        // Call #1 is GetRecipientAsync's own connection (must succeed, so the send is genuinely
        // reached); call #2 is MarkSentAsync's (must fail, after the "SMTP accepted" send already
        // happened) - this is the only way to reach the "accepted but not saved" branch, since SMTP
        // acceptance itself can't be faked to fail selectively at that exact moment any other way.
        var failingConnections = new FailFromNthCallConnectionFactory(_fixture.Connections, failFromCallNumber: 2);
        var repository = new NotificationRepository(failingConnections);
        var sender = new FakeMatchEmailSender();
        var delivery = new NotificationDeliveryService(
            repository, sender, NullLogger<NotificationDeliveryService>.Instance);

        var job = new NotificationJob(
            notificationId, matchId, finderId, NotificationTypes.MatchConfirmed, 1, leaseToken);

        // Does not throw - the whole point of this branch is that a DB failure after a real send
        // succeeds is logged, not surfaced as an exception the worker loop would otherwise retry.
        await delivery.DeliverAsync(job, CancellationToken.None);

        Assert.Single(sender.Sent);

        // Read back through the real, unwrapped connection - the row's own status was never
        // successfully updated (the write that would have done so is exactly what failed), so it must
        // still read PENDING, not SENT - proving this branch doesn't silently mislabel the outcome.
        Assert.Equal("PENDING", await ReadStatusAsync(notificationId));
    }
}
