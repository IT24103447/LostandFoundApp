using System.Text.Json;
using Confluent.Kafka;
using AdminVerifyService.Tests.Support;
using MySqlConnector;

namespace AdminVerifyService.Tests.Integration;

/// <summary>
/// Story LF-80, Step 10 - Kafka consumer integration. The REAL Program.cs host (AdminVerifyKafkaFactory)
/// with its real ListingEventConsumer hosted service, a real Kafka broker and a real MySQL. Events are
/// published to the broker in the ItemService outbox wire shape (camelCase JSON, key = event id) and
/// asserted via the app's own processed_events / tracked_listings / spam_records tables.
///
/// Every topic has exactly one partition, so per-topic FIFO ordering is strict. Tests use that order
/// for quiescence: a trailing message's DB row being present means every earlier message on that topic
/// was already consumed (and committed) - no fixed sleeps.
///
/// Pins over the live pipeline:
///  - created events (lost + found) are tracked end-to-end with the wire timestamp, not the clock;
///  - updated events never retrack (AlreadyTracked), but are still recorded as processed;
///  - an updated event for an unknown listing IS a new tracked listing;
///  - at-least-once replay of the same event id is exactly-once in the DB;
///  - resolved life-cycle events are NEVER consumed - the subscription excludes *.resolved;
///  - a burst of three posts reaches a real spam record (whole pipeline: consume -> parse -> track -> rule);
///  - poison payloads are skipped and committed, never fatal to the consumer.
/// </summary>
[Collection("AdminVerify Service Docker Integration")]
public sealed class ListingEventConsumerKafkaIntegrationTests : IClassFixture<AdminVerifyKafkaFactory>
{
    private const string LostCreatedTopic = "items.lost_item.created";
    private const string FoundCreatedTopic = "items.found_item.created";
    private const string LostUpdatedTopic = "items.lost_item.updated";
    private const string FoundUpdatedTopic = "items.found_item.updated";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuiescenceSettle = TimeSpan.FromSeconds(3);

    private readonly AdminVerifyKafkaFactory _factory;

    public ListingEventConsumerKafkaIntegrationTests(AdminVerifyKafkaFactory factory) => _factory = factory;

    // "2012-12-21T11:00:00Z" style wire values; DATETIME(3) round-trips whole seconds exactly.
    private static DateTime WholeSecondUtcNow()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    // ---- wire shape helpers (mirror ItemService OutboxEventPublisher: camelCase JSON, key = event id) ----

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static string Wire(object body) => JsonSerializer.Serialize(body, WireOptions);

    private static string CreatedPayload(Guid eventId, Guid userId, Guid listingId, DateTime createdAt, bool lost) =>
        Wire(new Dictionary<string, object>
        {
            ["eventId"] = eventId,
            ["eventType"] = lost ? "lost_item.created" : "found_item.created",
            ["timestamp"] = createdAt,
            ["userId"] = userId,
            [lost ? "lostItemId" : "foundItemId"] = listingId,
            ["createdAt"] = createdAt
        });

    private static string UpdatedPayload(Guid eventId, Guid userId, Guid listingId, DateTime updatedAt, bool lost) =>
        Wire(new Dictionary<string, object>
        {
            ["eventId"] = eventId,
            ["eventType"] = lost ? "lost_item.updated" : "found_item.updated",
            ["timestamp"] = updatedAt,
            ["userId"] = userId,
            [lost ? "lostItemId" : "foundItemId"] = listingId,
            ["updatedAt"] = updatedAt
        });

    private static string ResolvedPayload(Guid eventId, Guid listingId, bool lost) =>
        Wire(new Dictionary<string, object>
        {
            ["eventId"] = eventId,
            ["eventType"] = lost ? "lost_item.resolved" : "found_item.resolved",
            ["timestamp"] = DateTime.UtcNow,
            [lost ? "lostItemId" : "foundItemId"] = listingId
        });

    // ---- publish helper ----

    private async Task PublishAsync(string topic, string key, string json)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.BootstrapServers }).Build();

        var result = await producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = key,
            Value = json
        });

        producer.Flush(TimeSpan.FromSeconds(10));

        Assert.Equal(PersistenceStatus.Persisted, result.Status);
    }

    // ---- DB helpers (the app's own tables) ----

    private async Task ResetAsync()
    {
        await using var connection = new MySqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        // FK children first, then tracked_listings (referenced by spam_record_listings).
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

    private async Task<int> CountAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new MySqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<bool> ProcessedExistsAsync(Guid eventId) =>
        await CountAsync(
            "SELECT COUNT(*) FROM processed_events WHERE event_id = @eventId;",
            ("@eventId", eventId)) > 0;

    /// <summary>Blocks until the consumer has processed (and committed) the event, or fails the test.</summary>
    private async Task WaitForProcessedAsync(Guid eventId)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await ProcessedExistsAsync(eventId))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"Event {eventId} was not processed within {WaitTimeout}.");
    }

    private async Task<(string ListingType, Guid UserId, DateTime PostedAt)> ReadTrackedAsync(Guid listingId)
    {
        await using var connection = new MySqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT listing_type, user_id, posted_at FROM tracked_listings WHERE listing_id = @listingId;",
            connection);
        command.Parameters.AddWithValue("@listingId", listingId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new Xunit.Sdk.XunitException($"No tracked_listings row for listing {listingId}");
        }

        return (
            reader.GetString(0),
            reader.GetGuid(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    private sealed record SpamRecordRow(int ScoreA, string Status, Guid CollectingUserId, DateTime CollectingUntil);

    /// <summary>Blocks until a spam record exists for the user; returns its row or null on timeout.</summary>
    private async Task<SpamRecordRow?> WaitForSpamRecordAsync(Guid userId)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new MySqlConnection(_factory.ConnectionString);
            await connection.OpenAsync();

            await using var command = new MySqlCommand(
                """
                SELECT score_a, status, collecting_user_id, collecting_until
                FROM spam_records
                WHERE collecting_user_id = @userId;
                """,
                connection);
            command.Parameters.AddWithValue("@userId", userId);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new SpamRecordRow(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
            }

            await Task.Delay(200);
        }

        return null;
    }

    // ---- contract tests ----

    [Fact] // LF-80 AC: a live created event is tracked, stamped with the wire timestamp not the clock.
    public async Task KC01_LostCreatedEvent_IsConsumedAndTracked()
    {
        await ResetAsync();

        var now = WholeSecondUtcNow();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await PublishAsync(LostCreatedTopic, eventId.ToString(), CreatedPayload(eventId, userId, listingId, now, lost: true));
        await WaitForProcessedAsync(eventId);

        var tracked = await ReadTrackedAsync(listingId);
        Assert.Equal("LOST", tracked.ListingType);
        Assert.Equal(userId, tracked.UserId);
        Assert.Equal(now.Ticks, tracked.PostedAt.Ticks); // the wire createdAt, not the handler clock

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_records;")); // one listing is below any threshold
    }

    [Fact] // The found-side created topic takes the FOUND type end to end.
    public async Task KC02_FoundCreatedEvent_IsConsumedAndTrackedAsFound()
    {
        await ResetAsync();

        var now = WholeSecondUtcNow();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await PublishAsync(FoundCreatedTopic, eventId.ToString(), CreatedPayload(eventId, userId, listingId, now, lost: false));
        await WaitForProcessedAsync(eventId);

        var tracked = await ReadTrackedAsync(listingId);
        Assert.Equal("FOUND", tracked.ListingType);
        Assert.Equal(userId, tracked.UserId);
        Assert.Equal(now.Ticks, tracked.PostedAt.Ticks);
    }

    [Theory] // Both updated topics: the event is recorded as processed but never retracks.
    [InlineData(true)]
    [InlineData(false)]
    public async Task KC03_UpdatedEvent_ForKnownListing_IsNotTrackedAgain(bool lost)
    {
        await ResetAsync();

        var now = WholeSecondUtcNow();
        var userId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var createdEventId = Guid.NewGuid();
        var updatedEventId = Guid.NewGuid();
        var createdTopic = lost ? LostCreatedTopic : FoundCreatedTopic;
        var updatedTopic = lost ? LostUpdatedTopic : FoundUpdatedTopic;

        await PublishAsync(createdTopic, createdEventId.ToString(), CreatedPayload(createdEventId, userId, listingId, now, lost));
        await WaitForProcessedAsync(createdEventId);

        await PublishAsync(
            updatedTopic,
            updatedEventId.ToString(),
            UpdatedPayload(updatedEventId, userId, listingId, now.AddMinutes(10), lost));
        await WaitForProcessedAsync(updatedEventId);

        var tracked = await ReadTrackedAsync(listingId);
        Assert.Equal(lost ? "LOST" : "FOUND", tracked.ListingType);
        Assert.Equal(now.Ticks, tracked.PostedAt.Ticks); // the update did not rewrite posted_at

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM processed_events;")); // both events recorded
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_records;"));
    }

    [Fact] // At-least-once replay: the same event id delivered twice is exactly-once in the DB.
    public async Task KC04_RepeatedEventId_IsProcessedExactlyOnce()
    {
        await ResetAsync();

        var now = WholeSecondUtcNow();
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var trailerEventId = Guid.NewGuid();
        var trailerListingId = Guid.NewGuid();
        var json = CreatedPayload(eventId, userId, listingId, now, lost: true);

        // Same key -> same partition; the trailer proves BOTH copies of eventId were consumed before it.
        await PublishAsync(LostCreatedTopic, eventId.ToString(), json);
        await PublishAsync(LostCreatedTopic, eventId.ToString(), json);
        await PublishAsync(LostCreatedTopic, trailerEventId.ToString(), CreatedPayload(trailerEventId, userId, trailerListingId, now, lost: true));
        await WaitForProcessedAsync(trailerEventId);

        // The replay was a Duplicate (rolled back) - the event appears exactly once.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM processed_events WHERE event_id = @id;", ("@id", eventId)));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM tracked_listings WHERE listing_id = @id;", ("@id", listingId)));

        // And the whole topic contributes exactly what was published: 2 events, 2 listings.
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_records;"));
    }

    [Theory] // LF-80 "resolved-never-consumed": the lifecycle topics are never subscribed.
    [InlineData(true)]
    [InlineData(false)]
    public async Task KC05_ResolvedTopicEvent_IsNeverConsumed(bool lost)
    {
        await ResetAsync();

        var userId = Guid.NewGuid();
        var resolvedEventId = Guid.NewGuid();
        var resolvedListingId = Guid.NewGuid();
        var resolvedTopic = lost
            ? AdminVerifyKafkaFactory.LostItemResolvedTopic
            : AdminVerifyKafkaFactory.FoundItemResolvedTopic;

        // Publish the resolved event BEFORE the control, on its own unsubscribed topic, so no timing
        // choice can ever make it look consumed by accident.
        await PublishAsync(resolvedTopic, resolvedEventId.ToString(), ResolvedPayload(resolvedEventId, resolvedListingId, lost));

        var now = WholeSecondUtcNow();
        var controlEventId = Guid.NewGuid();
        var controlListingId = Guid.NewGuid();
        await PublishAsync(
            lost ? LostCreatedTopic : FoundCreatedTopic,
            controlEventId.ToString(),
            CreatedPayload(controlEventId, userId, controlListingId, now, lost));
        await WaitForProcessedAsync(controlEventId);

        // Give the consumer every chance to (wrongly) process the resolved event. It never does: the
        // subscription excludes *.resolved, so the broker never delivers that topic to this group.
        await Task.Delay(QuiescenceSettle);

        Assert.False(await ProcessedExistsAsync(resolvedEventId), "a resolved event must never be consumed");
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM tracked_listings WHERE listing_id = @id;", ("@id", resolvedListingId)));

        // The control proves the consumer is alive and the absence above is the subscription, not a
        // dead consumer.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM tracked_listings WHERE listing_id = @id;", ("@id", controlListingId)));
    }

    [Fact] // The money test: whole pipeline (Kafka -> parser -> handler -> LF-82 rule) reaches a real record.
    public async Task KC06_BurstOfThree_CreatesASpamRecordEndToEnd()
    {
        await ResetAsync();

        var userId = Guid.NewGuid();
        var now = WholeSecondUtcNow();

        var bursts = new (Guid EventId, Guid ListingId, DateTime PostedAt)[3];
        for (var i = 0; i < bursts.Length; i++)
        {
            bursts[i] = (Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(i * 5));
        }

        foreach (var (eventId, listingId, postedAt) in bursts)
        {
            await PublishAsync(LostCreatedTopic, eventId.ToString(), CreatedPayload(eventId, userId, listingId, postedAt, lost: true));
        }

        // The record only appears when the third event is handled (single-partition FIFO).
        var record = await WaitForSpamRecordAsync(userId);
        Assert.NotNull(record);

        Assert.Equal(3, record.ScoreA);
        Assert.Equal("NEEDS_REVIEW", record.Status);
        Assert.Equal(userId, record.CollectingUserId);
        Assert.Equal(bursts[2].PostedAt.AddMinutes(60).Ticks, record.CollectingUntil.Ticks); // postedAt + window

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM processed_events;"));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM spam_record_listings;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_record_notifications;")); // no recipients configured
    }

    [Theory] // Garbage must be skipped + committed, never fatal, never side-effecting.
    [InlineData("{ not json }")]
    [InlineData("")]
    [InlineData("{\"eventId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"eventType\":\"lost_item.created\",\"timestamp\":\"2026-06-01T12:00:00Z\",\"userId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"}")]
    public async Task KC07_PoisonPayload_IsSkippedAndTheConsumerSurvives(string poisonJson)
    {
        await ResetAsync();

        // Poison first, then a valid control on the same single-partition topic: the control being
        // processed proves the consumer moved past the poison and committed it without side effects.
        await PublishAsync(LostCreatedTopic, "poison", poisonJson);

        var now = WholeSecondUtcNow();
        var controlEventId = Guid.NewGuid();
        var controlListingId = Guid.NewGuid();
        await PublishAsync(LostCreatedTopic, controlEventId.ToString(), CreatedPayload(controlEventId, Guid.NewGuid(), controlListingId, now, lost: true));
        await WaitForProcessedAsync(controlEventId);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM processed_events;")); // only the control
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_records;"));
    }

    [Fact] // An updated event for a listing never seen is a NewPost (posted_at = updatedAt on updated topics).
    public async Task KC08_UpdatedEvent_ForUnknownListing_IsTrackedAsNew()
    {
        await ResetAsync();

        var now = WholeSecondUtcNow();
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        await PublishAsync(LostUpdatedTopic, eventId.ToString(), UpdatedPayload(eventId, userId, listingId, now, lost: true));
        await WaitForProcessedAsync(eventId);

        var tracked = await ReadTrackedAsync(listingId);
        Assert.Equal("LOST", tracked.ListingType);
        Assert.Equal(now.Ticks, tracked.PostedAt.Ticks); // posted_at carries the updatedAt value

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM tracked_listings;"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM spam_records;"));
    }
}