using AdminVerifyService.Configuration;
using AdminVerifyService.Databases;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-82 contract tests for SpamRule.ApplyAsync against a real MySQL (Testcontainers, the
/// app's real migrations, MySqlConnector transactions). Pins the LF-82 burst-rule semantics:
///
///  - free-listing count below SpamThreshold -> NotFlagged, nothing written;
///  - threshold reached -> one spam_records row (score = collected count, NEEDS_REVIEW, open to
///    further listings) + one link per collected listing + one notification per recipient;
///  - an open NEEDS_REVIEW record inside its window and under the cap absorbs the new listing
///    (JoinedRecord, score +1) instead of creating a second record;
///  - a record that is expired (postedAt &gt;= collectingUntil), full (score &gt;= cap) or no longer
///    NEEDS_REVIEW is first cleared of its collecting claim, then the rule re-evaluates the burst -
///    a record never collects forever and a user never has two collecting records;
///  - listings already linked to any record are never counted into a new burst (NOT EXISTS);
///  - ADD 1: two concurrent applications for one user serialize on the collecting record's
///    FOR UPDATE lock - exactly ONE collecting record survives, never two.
///
/// Tests call the rule directly with their own explicit transaction (exactly how the LF-80
/// event handler runs it) rather than going through the handler, whose intake paths are already
/// pinned by ListingEventHandlerTests (LF-80).
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class SpamRuleTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid UserA = Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("10000000-0000-0000-0000-00000000000b");

    private static readonly DateTime RefNow = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);

    private const string AdminOne = "admin1@example.com";
    private const string AdminTwo = "admin2@example.com";

    private record NotificationRow(string RecipientEmail, string Status, int Attempts);

    private record SpamRecordRow(Guid Id, int ScoreA, string Status, Guid? CollectingUserId, DateTime CollectingUntil);

    /// <summary>Minimal monitor over a fixed DetectionSettings instance - production DI binding and
    /// validation are pinned by DetectionSettingsTests (Step 3), not repeated here.</summary>
    private sealed class SettingsMonitor(DetectionSettings value) : IOptionsMonitor<DetectionSettings>
    {
        public DetectionSettings CurrentValue { get; } = value;

        public DetectionSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DetectionSettings, string?> listener) => null;
    }

    private static DetectionSettings Settings(int threshold = 3, int windowMinutes = 60, int cap = 0) => new()
    {
        SpamThreshold = threshold,
        SpamWindowMinutes = windowMinutes,
        SpamRecordCap = cap
    };

    private readonly AdminVerifyDbFixture _fixture;

    public SpamRuleTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private SpamRule Rule(DetectionSettings settings) =>
        new(
            new SpamRecordRepository(),
            new SettingsMonitor(settings),
            NullLogger<SpamRule>.Instance);

    private static ListingEvent Event(
        Guid userId,
        Guid listingId,
        DateTime postedAt,
        Guid? eventId = null) =>
        new(
            eventId ?? Guid.NewGuid(),
            "items.lost_item.created",
            listingId,
            ListingType.Lost,
            userId,
            postedAt);

    /// <summary>Applies the rule in its own explicit transaction (the handler pattern) and commits.</summary>
    private async Task<SpamRuleOutcome> ApplyAsync(
        SpamRule rule,
        ListingEvent evt,
        DateTime now)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var outcome = await rule.ApplyAsync(evt, now, connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();

        return outcome;
    }

    // ---- seeding helpers ----

    private static async Task ResetAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        // FK child tables first, then tracked_listings (referenced by spam_record_listings).
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

    private static async Task SeedTrackedAsync(
        IDbConnectionFactory connections,
        Guid listingId,
        Guid userId,
        DateTime postedAt)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, 'LOST', @userId, @postedAt, @now);
            """,
            connection);

        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@postedAt", postedAt);
        command.Parameters.AddWithValue("@now", postedAt);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedRecipientsAsync(IDbConnectionFactory connections, params string[] emails)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        foreach (var email in emails)
        {
            await using var command = new MySqlCommand(
                "INSERT INTO spam_alert_recipients (email) VALUES (@email);",
                connection);
            command.Parameters.AddWithValue("@email", email);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Seeds a spam_records row that is open to collection (collecting_user_id = the user)
    /// plus, optionally, tracked_listings rows and their spam_record_listings links.</summary>
    private static async Task SeedCollectingRecordAsync(
        IDbConnectionFactory connections,
        Guid recordId,
        Guid userId,
        int scoreA,
        string status,
        DateTime collectingUntil,
        IReadOnlyList<(Guid ListingId, DateTime PostedAt)>? links = null)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var record = new MySqlCommand(
            """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@id, @userId, @userId, @scoreA, @status, @collectingUntil, @now, @now);
            """,
            connection);

        record.Parameters.AddWithValue("@id", recordId);
        record.Parameters.AddWithValue("@userId", userId);
        record.Parameters.AddWithValue("@scoreA", scoreA);
        record.Parameters.AddWithValue("@status", status);
        record.Parameters.AddWithValue("@collectingUntil", collectingUntil);
        record.Parameters.AddWithValue("@now", RefNow);

        await record.ExecuteNonQueryAsync();

        if (links is null)
        {
            return;
        }

        foreach (var (listingId, postedAt) in links)
        {
            await using var tracked = new MySqlCommand(
                """
                INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
                VALUES (@listingId, 'LOST', @userId, @postedAt, @now);
                """,
                connection);
            tracked.Parameters.AddWithValue("@listingId", listingId);
            tracked.Parameters.AddWithValue("@userId", userId);
            tracked.Parameters.AddWithValue("@postedAt", postedAt);
            tracked.Parameters.AddWithValue("@now", RefNow);
            await tracked.ExecuteNonQueryAsync();

            await using var link = new MySqlCommand(
                "INSERT INTO spam_record_listings (spam_record_id, listing_id, added_at) VALUES (@recordId, @listingId, @now);",
                connection);
            link.Parameters.AddWithValue("@recordId", recordId);
            link.Parameters.AddWithValue("@listingId", listingId);
            link.Parameters.AddWithValue("@now", RefNow);
            await link.ExecuteNonQueryAsync();
        }
    }

    // ---- read helpers ----

    private static async Task<int> CountAsync(IDbConnectionFactory connections, string sql)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<SpamRecordRow> ReadSpamRecordAsync(
        IDbConnectionFactory connections,
        Guid recordId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT id, score_a, status, collecting_user_id, collecting_until FROM spam_records WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("@id", recordId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No spam_records row for record {recordId}");
        }

        var collectingUserId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);

        return new SpamRecordRow(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetString(2),
            collectingUserId,
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    private static async Task<IReadOnlyList<NotificationRow>> ReadNotificationsAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT recipient_email, status, attempts FROM spam_record_notifications ORDER BY recipient_email;",
            connection);

        var rows = new List<NotificationRow>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new NotificationRow(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    // ---- contract tests ----

    [Fact] // LF-82 AC: below-threshold traffic is absorbed - no record, no links, no notifications.
    public async Task SR01_BelowThreshold_IsNotFlagged_NothingWritten()
    {
        await ResetAsync(_fixture.Connections);

        var first = Guid.Parse("10000000-0000-0000-0000-100000000001");
        var second = Guid.Parse("10000000-0000-0000-0000-100000000002");
        await SeedTrackedAsync(_fixture.Connections, first, UserA, RefNow.AddMinutes(-40));
        await SeedTrackedAsync(_fixture.Connections, second, UserA, RefNow.AddMinutes(-5));

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 3)),
            Event(UserA, second, RefNow.AddMinutes(-5)),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_notifications;"));
    }

    [Fact] // LF-82 AC: at threshold the burst becomes one NEEDS_REVIEW record open to further joins.
    public async Task SR02_AtThreshold_CreatesRecord_WithScoreLinksAndNotifications()
    {
        await ResetAsync(_fixture.Connections);
        await SeedRecipientsAsync(_fixture.Connections, AdminOne, AdminTwo);

        var first = Guid.Parse("10000000-0000-0000-0000-200000000001");
        var second = Guid.Parse("10000000-0000-0000-0000-200000000002");
        await SeedTrackedAsync(_fixture.Connections, first, UserA, RefNow.AddMinutes(-5));
        await SeedTrackedAsync(_fixture.Connections, second, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, second, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.CreatedRecord, outcome);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));

        var recordId = Guid.Parse(await ScalarAsync(_fixture.Connections, "SELECT id FROM spam_records LIMIT 1;"));
        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(2, record.ScoreA);
        Assert.Equal(SpamRecordStatus.NeedsReview, record.Status);
        Assert.Equal(UserA, record.CollectingUserId);
        Assert.Equal(RefNow.Add(DefaultWindow).Ticks, record.CollectingUntil.Ticks);

        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));

        var notifications = await ReadNotificationsAsync(_fixture.Connections);
        Assert.Equal(new[] { AdminOne, AdminTwo }, notifications.Select(n => n.RecipientEmail).ToArray());
        Assert.All(notifications, n =>
        {
            Assert.Equal("PENDING", n.Status);
            Assert.Equal(0, n.Attempts);
        });
    }

    [Fact] // LF-82 AC: an open NEEDS_REVIEW record inside its window absorbs the next listing.
    public async Task SR03_OpenCollectingRecord_WithinWindow_Joins_NotCreates()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var linked = Guid.Parse("10000000-0000-0000-0000-300000000001");
        var fresh = Guid.Parse("10000000-0000-0000-0000-300000000002");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 1, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow.AddMinutes(30),
            links: new[] { (linked, RefNow.AddMinutes(-40)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.JoinedRecord, outcome);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(2, record.ScoreA);                 // 1 collected + 1 joined
        Assert.Equal(SpamRecordStatus.NeedsReview, record.Status);
        Assert.Equal(UserA, record.CollectingUserId);   // still open, still collecting

        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // LF-82 AC: an expired collecting window clears the claim, then the burst is re-evaluated.
    public async Task SR04_ExpiredCollectingRecord_IsStopped_ThenReEvaluated()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        var linked = Guid.Parse("10000000-0000-0000-0000-400000000001");
        var fresh = Guid.Parse("10000000-0000-0000-0000-400000000002");
        // collecting window ended a minute ago: the new listing (posted now) is NOT < collectingUntil.
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 1, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow.AddMinutes(-1),
            links: new[] { (linked, RefNow.AddHours(-2)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(1, record.ScoreA);                  // no join happened
        Assert.Null(record.CollectingUserId);            // collecting claim cleared
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;")); // no second record
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // LF-82 AC: a full record (score >= cap) can no longer join and its claim is cleared.
    public async Task SR05_FullRecord_CapReached_StopsCollecting_NoSecondRecord()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var linkedA = Guid.Parse("10000000-0000-0000-0000-500000000001");
        var linkedB = Guid.Parse("10000000-0000-0000-0000-500000000002");
        var fresh = Guid.Parse("10000000-0000-0000-0000-500000000003");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 2, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow.AddMinutes(30),
            links: new[] { (linkedA, RefNow.AddMinutes(-40)), (linkedB, RefNow.AddMinutes(-20)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        // cap = 2 == score -> CanJoin is false (score must be < cap).
        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2, cap: 2)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(2, record.ScoreA);
        Assert.Null(record.CollectingUserId);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // LF-82 AC: a record below its cap still absorbs listings - cap is an upper bound, not a cliff.
    public async Task SR06_BelowCap_StillJoins_UpToTheLimit()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000004");
        var linked = Guid.Parse("10000000-0000-0000-0000-600000000001");
        var fresh = Guid.Parse("10000000-0000-0000-0000-600000000002");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 2, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow.AddMinutes(30),
            links: new[] { (linked, RefNow.AddMinutes(-40)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        // cap = 3 > score 2 -> joins, reaching exactly the cap.
        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2, cap: 3)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.JoinedRecord, outcome);

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(3, record.ScoreA);
        Assert.Equal(UserA, record.CollectingUserId);
    }

    [Fact] // A record no longer awaiting review stops collecting before any new burst maths.
    public async Task SR07_NonReviewStatus_StopsCollecting()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000005");
        var linked = Guid.Parse("10000000-0000-0000-0000-700000000001");
        var fresh = Guid.Parse("10000000-0000-0000-0000-700000000002");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 1, SpamRecordStatus.UnderReview,
            collectingUntil: RefNow.AddMinutes(30),
            links: new[] { (linked, RefNow.AddMinutes(-40)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 3)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Null(record.CollectingUserId);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
    }

    [Fact] // Window boundary is strict '<': an event posted exactly at collecting_until does not join.
    public async Task SR08_WindowBoundary_ExactlyAtCollectingUntil_DoesNotJoin()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000006");
        var linked = Guid.Parse("10000000-0000-0000-0000-800000000001");
        var boundary = Guid.Parse("10000000-0000-0000-0000-800000000002");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserA, scoreA: 1, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow, // collecting ends exactly now
            links: new[] { (linked, RefNow.AddHours(-2)) });
        await SeedTrackedAsync(_fixture.Connections, boundary, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, boundary, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(1, record.ScoreA);
        Assert.Null(record.CollectingUserId);
    }

    [Fact] // A listing already linked to ANY record is never recounted into a new burst.
    public async Task SR09_LinkedListings_AreNeverReCounted_IntoANewBurst()
    {
        await ResetAsync(_fixture.Connections);

        // A former, closed record for the same user has already consumed linked listing A.
        var formerId = Guid.Parse("30000000-0000-0000-0000-000000000007");
        var consumed = Guid.Parse("10000000-0000-0000-0000-900000000001");
        var fresh = Guid.Parse("10000000-0000-0000-0000-900000000002");
        await SeedCollectingRecordAsync(
            _fixture.Connections, formerId, UserA, scoreA: 1, SpamRecordStatus.Dismissed,
            collectingUntil: RefNow.AddMinutes(-1),
            links: new[] { (consumed, RefNow.AddMinutes(-40)) });
        await SeedTrackedAsync(_fixture.Connections, fresh, UserA, RefNow);

        // LockCollecting finds no open claim (dismissed, collecting_user_id NULL). Without the
        // NOT EXISTS exclusion, the free count would be {consumed, fresh} = 2 = threshold -> record.
        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, fresh, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.NotFlagged, outcome);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // Creation is safe with an empty recipient table - notifications come from the recipients.
    public async Task SR10_NoConfiguredRecipients_QueuesNoNotifications()
    {
        await ResetAsync(_fixture.Connections);

        var first = Guid.Parse("10000000-0000-0000-0000-10000000000a");
        var second = Guid.Parse("10000000-0000-0000-0000-10000000000b");
        await SeedTrackedAsync(_fixture.Connections, first, UserA, RefNow.AddMinutes(-5));
        await SeedTrackedAsync(_fixture.Connections, second, UserA, RefNow);

        var outcome = await ApplyAsync(
            Rule(Settings(threshold: 2)),
            Event(UserA, second, RefNow),
            RefNow);

        Assert.Equal(SpamRuleOutcome.CreatedRecord, outcome);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_notifications;"));
    }

    [Fact] // ADD 1: concurrent applications for one user keep exactly ONE collecting record - the
    // FOR UPDATE lock on the collecting row serialises the joins (the second sees the first's
    // committed increment), so no second record is ever created and no update is lost.
    public async Task SR11_ConcurrentApplication_KeepsExactlyOneCollectingRecord()
    {
        await ResetAsync(_fixture.Connections);

        var recordId = Guid.Parse("30000000-0000-0000-0000-000000000008");
        var linked = Guid.Parse("10000000-0000-0000-0000-20000000000a");
        var burstA = Guid.Parse("10000000-0000-0000-0000-20000000000b");
        var burstB = Guid.Parse("10000000-0000-0000-0000-20000000000c");
        await SeedCollectingRecordAsync(
            _fixture.Connections, recordId, UserB, scoreA: 1, SpamRecordStatus.NeedsReview,
            collectingUntil: RefNow.AddMinutes(30),
            links: new[] { (linked, RefNow.AddMinutes(-40)) });
        await SeedTrackedAsync(_fixture.Connections, burstA, UserB, RefNow.AddMinutes(-5));
        await SeedTrackedAsync(_fixture.Connections, burstB, UserB, RefNow);

        var rule = Rule(Settings(threshold: 2));

        var a = ApplyAsync(rule, Event(UserB, burstA, RefNow.AddMinutes(-5)), RefNow);
        var b = ApplyAsync(rule, Event(UserB, burstB, RefNow), RefNow);

        var outcomes = await Task.WhenAll(a, b);

        Assert.All(outcomes, o => Assert.Equal(SpamRuleOutcome.JoinedRecord, o));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));

        var record = await ReadSpamRecordAsync(_fixture.Connections, recordId);
        Assert.Equal(3, record.ScoreA);          // 1 seeded + 2 concurrent joins, none lost
        Assert.Equal(UserB, record.CollectingUserId);

        Assert.Equal(3, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    private static async Task<string> ScalarAsync(IDbConnectionFactory connections, string sql)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToString(value) ?? throw new Xunit.Sdk.XunitException("Scalar query returned null.");
    }
}