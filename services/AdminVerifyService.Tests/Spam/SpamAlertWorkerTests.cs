using AdminVerifyService.Databases;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit.Sdk;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-82, Step 8 - worker-level tests for the alert pipeline
/// (<see cref="SpamAlertWorker"/> consuming <see cref="SpamAlertRepository"/>/sender) against
/// a real MySQL (Testcontainers + the app's real migrations).
///
/// The sender is a stub: Step 1 pins the real message-building and the http(s) guard, and a
/// real SMTP send stays gated on the Mailtrap account being verified (environmental), so live
/// delivery evidence is out of scope by design. These tests pin the worker *loop* semantics:
/// a due job is claimed and sent; a failed send schedules a retry; five consecutive failures
/// move the row to FAILED; an empty queue never touches the sender; shutdown is graceful.
///
/// Workers are started and hard-stopped inside each test (try/finally) so no loop outlives a
/// test and the shared per-class database stays deterministic.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamAlertWorkerTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid RecordUserId = Guid.Parse("10000000-0000-0000-0000-0000000000b1");
    private static readonly DateTime RefNow = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string NotifEmail = "admin@example.com";

    private readonly AdminVerifyDbFixture _fixture;

    public SpamAlertWorkerTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private sealed class MutableTimeProvider(DateTime start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(_now, TimeSpan.Zero);
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _now = _now.Add(by);
            }
        }
    }

    /// <summary>Records every delivery attempt; optionally fails them all with a fixed exception.</summary>
    private sealed class StubSender(Exception? failure = null) : ISpamAlertEmailSender
    {
        public int Called { get; private set; }

        public List<SpamAlertJob> Sent { get; } = [];

        public Task SendAsync(SpamAlertJob job, CancellationToken cancellationToken)
        {
            Called++;
            if (failure is not null)
            {
                throw failure;
            }

            Sent.Add(job);
            return Task.CompletedTask;
        }
    }

    private record NotificationRow(string Status, int Attempts, DateTime? NextAttemptAt, DateTime? SentAt);

    // ---- seeding / read helpers ----

    private static async Task ResetAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            DELETE FROM spam_record_notifications;
            DELETE FROM spam_record_listings;
            DELETE FROM spam_records;
            DELETE FROM spam_alert_recipients;
            DELETE FROM tracked_listings;
            DELETE FROM processed_events;
            """,
            connection);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedRecordAndNotificationAsync(
        IDbConnectionFactory connections,
        Guid notificationId,
        string status,
        int attempts,
        DateTime? nextAttemptAt)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        var recordId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var now = DateTime.UtcNow;

        await using var record = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, NULL, 0, 'NEEDS_REVIEW', @until, @now, @now);
            """,
            connection);
        record.Parameters.AddWithValue("@id", recordId);
        record.Parameters.AddWithValue("@userId", RecordUserId);
        record.Parameters.AddWithValue("@until", now.AddMinutes(30));
        record.Parameters.AddWithValue("@now", now);
        await record.ExecuteNonQueryAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_record_notifications
                (id, spam_record_id, recipient_email, status, attempts, next_attempt_at, created_at, updated_at)
            VALUES
                (@id, @recordId, @email, @status, @attempts, @next, @now, @now);
            """,
            connection);
        command.Parameters.AddWithValue("@id", notificationId);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@email", NotifEmail);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@next", nextAttemptAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<NotificationRow> ReadNotificationAsync(
        IDbConnectionFactory connections,
        Guid notificationId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT status, attempts, next_attempt_at, sent_at FROM spam_record_notifications WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", notificationId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new XunitException($"No spam_record_notifications row for {notificationId}");
        }

        DateTime? ReadNullableDateTime(int ordinal) =>
            reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

        return new NotificationRow(
            reader.GetString(0),
            reader.GetInt32(1),
            ReadNullableDateTime(2),
            ReadNullableDateTime(3));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new XunitException("Timed out waiting for condition.");
    }

    // ---- worker tests ----

    [Fact] // A due job is claimed, sent, and the row transitions to SENT with the schedule cleared.
    public async Task W01_DeliversDueJob_AndMarksItSent()
    {
        await ResetAsync(_fixture.Connections);
        var notificationId = Guid.Parse("50000000-0000-0000-0000-000000000101");

        await SeedRecordAndNotificationAsync(
            _fixture.Connections, notificationId,
            status: "PENDING", attempts: 0, nextAttemptAt: DateTime.UtcNow.AddMinutes(-1));

        var stub = new StubSender();
        var worker = new SpamAlertWorker(
            new SpamAlertRepository(_fixture.Connections, TimeProvider.System),
            stub,
            NullLogger<SpamAlertWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => (await ReadNotificationAsync(_fixture.Connections, notificationId)).Status == "SENT");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var row = await ReadNotificationAsync(_fixture.Connections, notificationId);
        Assert.Equal("SENT", row.Status);
        Assert.NotNull(row.SentAt);
        Assert.Null(row.NextAttemptAt);

        var job = Assert.Single(stub.Sent);
        Assert.Equal(notificationId, job.Id);           // the claimed row was the one sent
        Assert.Equal(NotifEmail, job.RecipientEmail);
        Assert.Equal(1, job.Attempts);                  // first attempt
    }

    [Fact] // A failed send leaves the row PENDING with a future retry scheduled (backoff lives in the repo).
    public async Task W02_FailedSend_SchedulesBackoff_KeepsPending()
    {
        await ResetAsync(_fixture.Connections);
        var notificationId = Guid.Parse("50000000-0000-0000-0000-000000000102");

        await SeedRecordAndNotificationAsync(
            _fixture.Connections, notificationId,
            status: "PENDING", attempts: 0, nextAttemptAt: DateTime.UtcNow.AddMinutes(-1));

        var stub = new StubSender(new InvalidOperationException("SMTP unreachable"));
        var worker = new SpamAlertWorker(
            new SpamAlertRepository(_fixture.Connections, TimeProvider.System),
            stub,
            NullLogger<SpamAlertWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () =>
            {
                var row = await ReadNotificationAsync(_fixture.Connections, notificationId);
                return row.Attempts == 1 && row.Status == "PENDING" && row.NextAttemptAt is not null;
            });
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var row = await ReadNotificationAsync(_fixture.Connections, notificationId);
        Assert.Equal("PENDING", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.NextAttemptAt);
        Assert.True(row.NextAttemptAt > DateTime.UtcNow.AddMinutes(1)); // 2-minute retry still ahead
        Assert.Equal(1, stub.Called); // exactly one delivery attempt happened
    }

    [Fact] // After MaxAttempts (5) consecutive failures the row is FAILED with nothing scheduled.
    public async Task W03_GivesUpAfterFiveFailedAttempts_MarksFailed()
    {
        await ResetAsync(_fixture.Connections);
        var notificationId = Guid.Parse("50000000-0000-0000-0000-000000000103");
        var time = new MutableTimeProvider(RefNow);

        await SeedRecordAndNotificationAsync(
            _fixture.Connections, notificationId,
            status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var stub = new StubSender(new InvalidOperationException("SMTP unreachable"));
        var worker = new SpamAlertWorker(
            new SpamAlertRepository(_fixture.Connections, time),
            stub,
            NullLogger<SpamAlertWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Attempt 1: the seeded row is already due (next_attempt_at = RefNow - 1 min).
            await WaitUntilAsync(async () => (await ReadNotificationAsync(_fixture.Connections, notificationId)).Attempts >= 1);

            for (var expect = 2; expect <= SpamAlertRepository.MaxAttempts; expect++)
            {
                var isLast = expect == SpamAlertRepository.MaxAttempts;

                // Make the row due again by advancing the clock past its current retry time
                // (read back from the database, so the advance targets the real value the
                // worker will test), then wait for the worker's next claim. The worker's 5 s
                // idle sleep is inside the 15 s wait below; each advance is a no-op if the row
                // is already due.
                var row = await ReadNotificationAsync(_fixture.Connections, notificationId);
                var delta = row.NextAttemptAt!.Value.AddMinutes(1) - time.GetUtcNow().UtcDateTime;
                if (delta > TimeSpan.Zero)
                {
                    time.Advance(delta);
                }

                await WaitUntilAsync(async () =>
                {
                    var current = await ReadNotificationAsync(_fixture.Connections, notificationId);
                    return current.Attempts >= expect &&
                           (isLast ? current.Status == "FAILED"
                                   : current.Status == "PENDING" && current.NextAttemptAt is not null);
                });
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var final = await ReadNotificationAsync(_fixture.Connections, notificationId);
        Assert.Equal("FAILED", final.Status);
        Assert.Equal(5, final.Attempts);
        Assert.Null(final.NextAttemptAt);
        Assert.Equal(5, stub.Called); // exactly the retry budget was spent, then it stopped
    }

    [Fact] // An empty queue means the loop idles - the sender is never invoked.
    public async Task W04_DoesNotCallSender_WhenNothingIsDue()
    {
        await ResetAsync(_fixture.Connections);

        var stub = new StubSender();
        var worker = new SpamAlertWorker(
            new SpamAlertRepository(_fixture.Connections, TimeProvider.System),
            stub,
            NullLogger<SpamAlertWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(1500); // comfortably inside the idle delay; the claim query runs repeatedly
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Empty(stub.Sent);
        Assert.Equal(0, stub.Called);
    }
}