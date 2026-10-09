using AdminVerifyService.Configuration;
using AdminVerifyService.Databases;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;
using AdminVerifyService.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AdminVerifyService.Tests.Listings;

/// <summary>
/// Story LF-80 contract tests for ListingEventHandler.HandleAsync against a real MySQL
/// (Testcontainers, the app's real migrations, transactions from MySqlConnector):
///
///  - a never-seen event is a NewPost: the tracked_listings row and the processed_events de-dupe
///    row are written inside the same transaction, stamped with the injected TimeProvider's clock;
///  - replaying the same event_id is a Duplicate (INSERT IGNORE on the processed_events PK) and
///    rolls the whole transaction back - nothing can be double-counted;
///  - an event whose listing_id is already tracked is AlreadyTracked: the event is still recorded
///    as processed (so it is never re-tried) but the tracked_listings row is left untouched;
///  - NewPost feeds the LF-82 SpamRule, so a real concurrent burst reaches a real spam_record;
///  - concurrent delivery of the same event yields exactly one NewPost and one Duplicate.
///
/// The SpamRule behaviour in depth (window joins, caps, collecting records) is owned by the LF-82
/// rule tests (Step 7); here the SpamRule instance is real and wired with a stub
/// IOptionsMonitor - DI binding/validation of DetectionSettings is already pinned by
/// DetectionSettingsTests (Step 3), so this class does not re-declare the validation predicate.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class ListingEventHandlerTests : IClassFixture<AdminVerifyDbFixture>
{
    private static readonly Guid UserA = Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("20000000-0000-0000-0000-00000000000b");

    // Whole-second, offset-free instants so the DATETIME(3) round-trip is exact.
    private static readonly DateTime RefNow = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan DetectionWindow = TimeSpan.FromMinutes(60);

    private record ProcessedRow(string Topic, DateTime ProcessedAt);

    private record TrackedRow(string ListingType, Guid UserId, DateTime PostedAt, DateTime CreatedAt);

    private record SpamRecordRow(int ScoreA, string Status, Guid CollectingUserId, DateTime CollectingUntil);

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(RefNow);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Minimal monitor over a fixed DetectionSettings instance - production binding and
    /// validation are pinned by the Step 3 DetectionSettingsTests, not here.</summary>
    private sealed class SettingsMonitor(DetectionSettings value) : IOptionsMonitor<DetectionSettings>
    {
        public DetectionSettings CurrentValue { get; } = value;

        public DetectionSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DetectionSettings, string?> listener) => null;
    }

    private static DetectionSettings Settings(int threshold = 3) => new()
    {
        SpamThreshold = threshold,
        SpamWindowMinutes = (int)DetectionWindow.TotalMinutes,
        SpamRecordCap = 0
    };

    private readonly AdminVerifyDbFixture _fixture;

    public ListingEventHandlerTests(AdminVerifyDbFixture fixture) => _fixture = fixture;

    private ListingEventHandler Handler(TimeProvider time, DetectionSettings settings) =>
        new(
            _fixture.Connections,
            new TrackedListingRepository(),
            new SpamRule(
                new SpamRecordRepository(),
                new SettingsMonitor(settings),
                NullLogger<SpamRule>.Instance),
            time);

    private static ListingEvent Event(
        Guid userId,
        Guid? eventId = null,
        Guid? listingId = null,
        string topic = "items.lost_item.created",
        string listingType = ListingType.Lost,
        DateTime? postedAt = null) =>
        new(
            eventId ?? Guid.NewGuid(),
            topic,
            listingId ?? Guid.NewGuid(),
            listingType,
            userId,
            postedAt ?? RefNow);

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
            DELETE FROM tracked_listings;
            DELETE FROM processed_events;
            """,
            connection);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedTrackedListingAsync(
        IDbConnectionFactory connections,
        Guid listingId,
        Guid userId,
        DateTime postedAt,
        DateTime createdAt)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            """
            INSERT INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, 'LOST', @userId, @postedAt, @createdAt);
            """,
            connection);

        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@postedAt", postedAt);
        command.Parameters.AddWithValue("@createdAt", createdAt);

        await command.ExecuteNonQueryAsync();
    }

    // ---- read helpers ----

    private static async Task<int> CountAsync(IDbConnectionFactory connections, string sql)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<ProcessedRow> ReadProcessedAsync(
        IDbConnectionFactory connections,
        Guid eventId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT topic, processed_at FROM processed_events WHERE event_id = @eventId;",
            connection);
        command.Parameters.AddWithValue("@eventId", eventId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No processed_events row for event {eventId}");
        }

        return new ProcessedRow(
            reader.GetString(0),
            DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
    }

    private static async Task<TrackedRow> ReadTrackedAsync(
        IDbConnectionFactory connections,
        Guid listingId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT listing_type, user_id, posted_at, created_at FROM tracked_listings WHERE listing_id = @listingId;",
            connection);
        command.Parameters.AddWithValue("@listingId", listingId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No tracked_listings row for listing {listingId}");
        }

        return new TrackedRow(
            reader.GetString(0),
            reader.GetGuid(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
    }

    private static async Task<SpamRecordRow> ReadSingleSpamRecordAsync(IDbConnectionFactory connections)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT score_a, status, collecting_user_id, collecting_until FROM spam_records;",
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException("Expected exactly one spam_records row.");
        }

        return new SpamRecordRow(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetGuid(2),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
    }

    // ---- contract tests ----

    [Fact] // LF-80 AC: a fresh event is recorded as a tracked listing inside the intake transaction.
    public async Task EH01_FirstEvent_IsNewPost_PersistsListingAndProcessedEvent()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var evt = Event(UserA, listingId: Guid.Parse("10000000-0000-0000-0000-a00000000001"));
        var handler = Handler(time, Settings());

        var outcome = await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal(ListingEventOutcome.NewPost, outcome);

        var tracked = await ReadTrackedAsync(_fixture.Connections, evt.ListingId);
        Assert.Equal(ListingType.Lost, tracked.ListingType);
        Assert.Equal(UserA, tracked.UserId);
        Assert.Equal(evt.PostedAt.Ticks, tracked.PostedAt.Ticks);   // event timestamp, not the clock
        Assert.Equal(RefNow.Ticks, tracked.CreatedAt.Ticks);        // stamped with the handler clock

        var processed = await ReadProcessedAsync(_fixture.Connections, evt.EventId);
        Assert.Equal(evt.Topic, processed.Topic);
        Assert.Equal(RefNow.Ticks, processed.ProcessedAt.Ticks);
    }

    [Fact] // LF-80 AC: replaying the same event_id must be a Duplicate with zero side effects.
    public async Task EH02_ReplayedEvent_IsDuplicate_NoDoubleCounting()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var evt = Event(UserA);
        var handler = Handler(time, Settings());

        var first = await handler.HandleAsync(evt, CancellationToken.None);
        var second = await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal(ListingEventOutcome.NewPost, first);
        Assert.Equal(ListingEventOutcome.Duplicate, second);

        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // LF-80 AC: a re-published event for an already known listing is AlreadyTracked but still
    // recorded as processed, so the consumer never retries (and never re-feeds the detection rule).
    public async Task EH03_KnownListing_IsAlreadyTracked_EventStillRecordedAsProcessed()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var listingId = Guid.Parse("10000000-0000-0000-0000-b00000000001");
        var seededAt = RefNow.AddHours(-2);
        await SeedTrackedListingAsync(_fixture.Connections, listingId, UserA, seededAt, seededAt);

        var evt = Event(UserA, listingId: listingId);
        var handler = Handler(time, Settings());

        var outcome = await handler.HandleAsync(evt, CancellationToken.None);

        Assert.Equal(ListingEventOutcome.AlreadyTracked, outcome);

        var processed = await ReadProcessedAsync(_fixture.Connections, evt.EventId);
        Assert.Equal(evt.Topic, processed.Topic);

        var tracked = await ReadTrackedAsync(_fixture.Connections, listingId);
        Assert.Equal(seededAt.Ticks, tracked.CreatedAt.Ticks); // row untouched, no re-stamp

        // Replaying that event now sees the processed_events row -> Duplicate, still one row each.
        var replay = await handler.HandleAsync(evt, CancellationToken.None);
        Assert.Equal(ListingEventOutcome.Duplicate, replay);
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM tracked_listings;"));
    }

    [Fact] // LF-80 AC: normal (below-threshold) traffic is intake-correct - never a false record.
    public async Task EH04_NormalTraffic_BelowThreshold_CreatesNoSpamRecord()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var handler = Handler(time, Settings(threshold: 4)); // above the 3-listing pace below

        var first = Event(UserA, postedAt: RefNow);
        var second = Event(UserA, postedAt: RefNow.AddMinutes(5));
        var third = Event(UserA, postedAt: RefNow.AddMinutes(10));

        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(first, CancellationToken.None));
        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(second, CancellationToken.None));
        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(third, CancellationToken.None));

        Assert.Equal(3, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(3, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
    }

    [Fact] // LF-80 AC: a NewPost is handed to the LF-82 rule, so a real burst reaches a real record.
    public async Task EH05_ReachingTheThreshold_FeedsTheSpamRule_EndToEnd()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var handler = Handler(time, Settings(threshold: 2));

        var first = Event(UserA, postedAt: RefNow);
        var second = Event(UserA, postedAt: RefNow.AddMinutes(5));

        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(first, CancellationToken.None));
        Assert.Equal(
            ListingEventOutcome.NewPost,
            await handler.HandleAsync(second, CancellationToken.None));

        var record = await ReadSingleSpamRecordAsync(_fixture.Connections);
        Assert.Equal(2, record.ScoreA);
        Assert.Equal(SpamRecordStatus.NeedsReview, record.Status);
        Assert.Equal(UserA, record.CollectingUserId);
        Assert.Equal(second.PostedAt.Add(DetectionWindow).Ticks, record.CollectingUntil.Ticks);

        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));

        // No recipients are configured in this fixture, so no notifications are queued yet
        // (the alert pipeline is exercised in the LF-82 step).
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_notifications;"));
    }

    [Fact] // Duplicate replays must never re-feed the detection rule on an already flagged burst.
    public async Task EH06_ReplayedEvent_DoesNotFeedDetectionAgain()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var handler = Handler(time, Settings(threshold: 2));

        var first = Event(UserA, postedAt: RefNow);
        var second = Event(UserA, postedAt: RefNow.AddMinutes(5));

        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(first, CancellationToken.None));
        Assert.Equal(ListingEventOutcome.NewPost, await handler.HandleAsync(second, CancellationToken.None));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));

        Assert.Equal(ListingEventOutcome.Duplicate, await handler.HandleAsync(first, CancellationToken.None));
        Assert.Equal(ListingEventOutcome.Duplicate, await handler.HandleAsync(second, CancellationToken.None));

        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
    }

    [Fact] // At-least-once delivery: two consumers racing the same event -> exactly one win.
    public async Task EH07_ConcurrentDelivery_ProducesExactlyOneNewPost()
    {
        await ResetAsync(_fixture.Connections);

        var evt = Event(UserA, postedAt: RefNow.AddMinutes(2));

        var first = Handler(new MutableTimeProvider(), Settings(threshold: 100)).HandleAsync(evt, CancellationToken.None);
        var second = Handler(new MutableTimeProvider(), Settings(threshold: 100)).HandleAsync(evt, CancellationToken.None);

        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, o => o == ListingEventOutcome.NewPost);
        Assert.Single(outcomes, o => o == ListingEventOutcome.Duplicate);

        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
    }

    [Fact] // AlreadyTracked short-circuits before the rule - never a false record on a known listing.
    public async Task EH08_AlreadyTracked_DoesNotTriggerDetection()
    {
        await ResetAsync(_fixture.Connections);

        var time = new MutableTimeProvider();
        var handler = Handler(time, Settings(threshold: 2));

        var knownA = Guid.Parse("10000000-0000-0000-0000-c00000000001");
        var knownB = Guid.Parse("10000000-0000-0000-0000-c00000000002");
        await SeedTrackedListingAsync(_fixture.Connections, knownA, UserB, RefNow, RefNow);
        await SeedTrackedListingAsync(_fixture.Connections, knownB, UserB, RefNow.AddMinutes(5), RefNow);

        // Re-published event for one of the already tracked listings: the user's two free listings
        // would clear the threshold, but AlreadyTracked must return before the rule runs.
        var replay = Event(UserB, listingId: knownA, postedAt: RefNow.AddMinutes(10));
        var outcome = await handler.HandleAsync(replay, CancellationToken.None);

        Assert.Equal(ListingEventOutcome.AlreadyTracked, outcome);
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_records;"));
        Assert.Equal(0, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM spam_record_listings;"));
        Assert.Equal(1, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(2, await CountAsync(_fixture.Connections, "SELECT COUNT(*) FROM tracked_listings;"));
    }
}