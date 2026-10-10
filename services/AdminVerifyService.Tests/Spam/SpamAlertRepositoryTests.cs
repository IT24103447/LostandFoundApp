using AdminVerifyService.Databases;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using MySqlConnector;
using Xunit.Sdk;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-82, Step 8 - contract tests for the alert-pipeline claimer/marker
/// <see cref="SpamAlertRepository"/> against a real MySQL (Testcontainers + the app's real
/// migrations). Pins the claim / backoff / FAILED semantics:
///
///  - ClaimNextAsync picks the earliest due PENDING row (next_attempt_at &lt;= now, tie-broken
///    by id), returns it with attempts + 1, and leases the row for 5 minutes via an optimistic
///    next_attempt_at compare (a leased row cannot be double-claimed);
///  - rows not yet due, or already SENT/FAILED, are never claimed;
///  - MarkSentAsync -> SENT, sent_at set, schedule cleared;
///  - MarkFailedAttemptAsync stays PENDING with an exponential backoff (2^attempts minutes,
///    capped at 60) below MaxAttempts (5) and moves to FAILED with no schedule at the boundary.
///
/// The clock is a mutable TimeProvider so lease and backoff windows are asserted exactly -
/// no sleeps. The worker loop that consumes these primitives is covered separately by
/// SpamAlertWorkerTests.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamAlertRepositoryTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid RecordUserId = Guid.Parse("10000000-0000-0000-0000-0000000000a1");
    private static readonly DateTime RefNow = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string NotifEmail = "admin@example.com";

    private readonly AdminVerifyDbFixture _fixture;
    private readonly MutableTimeProvider _time;

    public SpamAlertRepositoryTests(AdminVerifyDbFixture fixture)
    {
        _fixture = fixture;
        _time = new MutableTimeProvider(RefNow);
    }

    /// <summary>A TimeProvider whose clock QA drives explicitly, so lease and backoff
    /// windows are asserted to the exact tick instead of slept through.</summary>
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

    private record NotificationRow(string Status, int Attempts, DateTime? NextAttemptAt, DateTime? SentAt);

    private SpamAlertRepository Repo() => new(_fixture.Connections, _time);

    private async Task<SpamAlertJob?> ClaimAsync(SpamAlertRepository repo) =>
        await repo.ClaimNextAsync(CancellationToken.None);

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

    private static async Task SeedRecordAsync(IDbConnectionFactory connections, Guid recordId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, NULL, 0, 'NEEDS_REVIEW', @until, @now, @now);
            """,
            connection);

        command.Parameters.AddWithValue("@id", recordId);
        command.Parameters.AddWithValue("@userId", RecordUserId);
        command.Parameters.AddWithValue("@until", RefNow.AddMinutes(30));
        command.Parameters.AddWithValue("@now", RefNow);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedNotificationAsync(
        IDbConnectionFactory connections,
        Guid recordId,
        Guid notificationId,
        string recipientEmail,
        string status,
        int attempts,
        DateTime? nextAttemptAt)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

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
        command.Parameters.AddWithValue("@email", recipientEmail);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@next", nextAttemptAt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@now", RefNow);

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

    // ---- contract tests ----

    [Fact] // Nothing to do and nothing to claim.
    public async Task R01_ClaimNext_EmptyTable_ReturnsNull()
    {
        await ResetAsync(_fixture.Connections);

        var job = await ClaimAsync(Repo());

        Assert.Null(job);
    }

    [Fact] // Only due PENDING rows exist in the claim query: future and terminal rows are ignored.
    public async Task R02_ClaimNext_SkipsFutureAndTerminalRows_ReturnsNull()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-000000000001");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, Guid.Parse("40000000-0000-0000-0000-100000000001"), "future@example.com", status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddHours(1)); // not due
        await SeedNotificationAsync(_fixture.Connections, recordId, Guid.Parse("40000000-0000-0000-0000-100000000002"), "sent@example.com", status: "SENT", attempts: 1, nextAttemptAt: null);
        await SeedNotificationAsync(_fixture.Connections, recordId, Guid.Parse("40000000-0000-0000-0000-100000000003"), "failed@example.com", status: "FAILED", attempts: 5, nextAttemptAt: null);

        var job = await ClaimAsync(Repo());

        Assert.Null(job);
    }

    [Fact] // The earliest due PENDING row is claimed; attempts +1; row leased for 5 minutes.
    public async Task R03_ClaimNext_ClaimsEarliestDueRow_AndLeasesIt()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-000000000002");
        var staleId = Guid.Parse("40000000-0000-0000-0000-000000000003");
        var newerId = Guid.Parse("40000000-0000-0000-0000-000000000004");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, staleId, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-10)); // earliest
        await SeedNotificationAsync(_fixture.Connections, recordId, newerId, "newer@example.com", status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-5));

        var job = await ClaimAsync(Repo());

        Assert.NotNull(job);
        Assert.Equal(staleId, job.Id);
        Assert.Equal(NotifEmail, job.RecipientEmail);
        Assert.Equal(1, job.Attempts); // 0 claimed + 1

        var row = await ReadNotificationAsync(_fixture.Connections, staleId);
        Assert.Equal("PENDING", row.Status);      // still pending while leased
        Assert.Equal(1, row.Attempts);
        Assert.Equal(RefNow.AddMinutes(5).Ticks, row.NextAttemptAt!.Value.Ticks); // claim lease = 5 min

        var untouched = await ReadNotificationAsync(_fixture.Connections, newerId);
        Assert.Equal(0, untouched.Attempts);
    }

    [Fact] // ORDER BY next_attempt_at, id breaks ties deterministically.
    public async Task R04_ClaimNext_TieBreakOnId_IsDeterministic()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-000000000005");
        var firstId = Guid.Parse("40000000-0000-0000-0000-000000000006");
        var secondId = Guid.Parse("40000000-0000-0000-0000-000000000007");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, firstId, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));
        await SeedNotificationAsync(_fixture.Connections, recordId, secondId, "second@example.com", status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var job = await ClaimAsync(Repo());

        Assert.NotNull(job);
        Assert.Equal(firstId, job.Id); // lower id wins at an identical next_attempt_at
    }

    [Fact] // The optimistic next_attempt_at compare prevents double claims inside one lease.
    public async Task R05_ClaimNext_NoDoubleClaim_WhileLeased()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-000000000008");
        var id = Guid.Parse("40000000-0000-0000-0000-000000000009");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, id, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var first = await ClaimAsync(Repo());
        Assert.NotNull(first);

        var second = await ClaimAsync(Repo()); // still inside the 5-minute lease at RefNow

        Assert.Null(second);
        Assert.Equal(1, (await ReadNotificationAsync(_fixture.Connections, id)).Attempts);
    }

    [Fact] // Once the lease expires the same row is claimed again with attempts incremented further.
    public async Task R06_ClaimNext_ReclaimsAfterLeaseExpiry_WithIncrementedAttempts()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-00000000000a");
        var id = Guid.Parse("40000000-0000-0000-0000-00000000000b");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, id, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var first = await ClaimAsync(Repo());
        Assert.NotNull(first);
        Assert.Equal(1, first.Attempts);

        _time.Advance(TimeSpan.FromMinutes(6)); // lease (5 min) has expired

        var second = await ClaimAsync(Repo());

        Assert.NotNull(second);
        Assert.Equal(2, second.Attempts);
        Assert.Equal(RefNow.AddMinutes(11).Ticks, (await ReadNotificationAsync(_fixture.Connections, id)).NextAttemptAt!.Value.Ticks);
    }

    [Fact] // A successful send transitions the row to SENT, stamps sent_at and clears the schedule.
    public async Task R07_MarkSent_TransitionsToSent_AndClearsSchedule()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-00000000000c");
        var id = Guid.Parse("40000000-0000-0000-0000-00000000000d");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, id, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var job = await ClaimAsync(Repo());
        Assert.NotNull(job);

        await Repo().MarkSentAsync(job, CancellationToken.None);

        var row = await ReadNotificationAsync(_fixture.Connections, id);
        Assert.Equal("SENT", row.Status);
        Assert.NotNull(row.SentAt);
        Assert.Equal(RefNow.Ticks, row.SentAt!.Value.Ticks);
        Assert.Null(row.NextAttemptAt); // nothing left to schedule
    }

    [Fact] // A failed send below MaxAttempts stays PENDING with exponential backoff 2^attempts.
    public async Task R08_MarkFailed_BelowMaxAttempts_RestoresPendingWithExponentialBackoff()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-00000000000e");
        var id = Guid.Parse("40000000-0000-0000-0000-00000000000f");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, id, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        // Attempt 1 fails -> backoff 2^1 = 2 minutes.
        var job1 = await ClaimAsync(Repo());
        Assert.NotNull(job1);
        await Repo().MarkFailedAttemptAsync(job1, CancellationToken.None);

        var row1 = await ReadNotificationAsync(_fixture.Connections, id);
        Assert.Equal("PENDING", row1.Status);
        Assert.Equal(2, TimeSpan.FromTicks(row1.NextAttemptAt!.Value.Ticks - _time.GetUtcNow().UtcDateTime.Ticks).Minutes);

        _time.Advance(TimeSpan.FromMinutes(3)); // past the 2-minute delay

        // Attempt 2 fails -> backoff 2^2 = 4 minutes from its failure moment.
        var job2 = await ClaimAsync(Repo());
        Assert.NotNull(job2);
        Assert.Equal(2, job2.Attempts);
        await Repo().MarkFailedAttemptAsync(job2, CancellationToken.None);

        var row2 = await ReadNotificationAsync(_fixture.Connections, id);
        Assert.Equal("PENDING", row2.Status);
        Assert.Equal(4, TimeSpan.FromTicks(row2.NextAttemptAt!.Value.Ticks - _time.GetUtcNow().UtcDateTime.Ticks).Minutes);
    }

    [Fact] // Exactly at MaxAttempts (5) a failed send gives up: FAILED, no schedule left.
    public async Task R09_MarkFailed_AtMaxAttempts_TransitionsToFailed()
    {
        await ResetAsync(_fixture.Connections);
        var recordId = Guid.Parse("40000000-0000-0000-0000-000000000010");
        var id = Guid.Parse("40000000-0000-0000-0000-000000000011");

        await SeedRecordAsync(_fixture.Connections, recordId);
        await SeedNotificationAsync(_fixture.Connections, recordId, id, NotifEmail, status: "PENDING", attempts: 0, nextAttemptAt: RefNow.AddMinutes(-1));

        var repo = Repo();

        for (var expect = 1; expect <= SpamAlertRepository.MaxAttempts; expect++)
        {
            var job = await ClaimAsync(repo);
            Assert.NotNull(job);
            Assert.Equal(expect, job.Attempts);

            await repo.MarkFailedAttemptAsync(job, CancellationToken.None);

            var row = await ReadNotificationAsync(_fixture.Connections, id);
            if (expect < SpamAlertRepository.MaxAttempts)
            {
                // Still retrying: PENDING with the exact exponential backoff 2^attempts.
                Assert.Equal("PENDING", row.Status);
                var delay = row.NextAttemptAt!.Value - _time.GetUtcNow().UtcDateTime;
                Assert.Equal(TimeSpan.FromMinutes(Math.Pow(2, expect)), delay);

                // Advance past this backoff so the next claim is due again.
                var delta = row.NextAttemptAt!.Value.AddMinutes(1) - _time.GetUtcNow().UtcDateTime;
                if (delta > TimeSpan.Zero)
                {
                    _time.Advance(delta);
                }
            }
            else
            {
                // Attempt 5 failed: give up.
                Assert.Equal("FAILED", row.Status);
                Assert.Null(row.NextAttemptAt);
            }
        }

        var final = await ReadNotificationAsync(_fixture.Connections, id);
        Assert.Equal("FAILED", final.Status);
        Assert.Equal(5, final.Attempts);
        Assert.Null(final.NextAttemptAt);
    }

    [Fact] // The retry budget is a public contract - lower/raising it changes the give-up boundary.
    public void R10_MaxAttempts_IsFive()
    {
        Assert.Equal(5, SpamAlertRepository.MaxAttempts);
    }
}