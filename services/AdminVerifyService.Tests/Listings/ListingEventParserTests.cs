using System.Text.Json;
using AdminVerifyService.Listings;

namespace AdminVerifyService.Tests.Listings;

/// <summary>
/// Story LF-80 pure, no-network coverage of ListingEventParser.Parse - the topic-driven mapping of
/// the ItemService outbox wire shape (camelCase JSON, serialised by OutboxEventPublisher with
/// JsonNamingPolicy.CamelCase) into a ListingEvent. The four subscribed topics are covered per
/// direction, along with every return-null guard. No Kafka, no database and no Docker, so this
/// class always runs in CI.
///
/// Timestamp pins deliberately use only Z-suffixed (Utc) and suffix-free (Unspecified) dates:
/// an offset-bearing timestamp is routed through the host time zone inside the parser before
/// ToUtc, so the exact UTC wall-clock it yields depends on the machine. The Kind=Utc result is
/// guaranteed for every input, but offset dates are left as a manual/known-gap check instead of
/// a CI-stable assertion (recorded in known-gaps, Story-LF-80).
/// </summary>
public sealed class ListingEventParserTests
{
    // Mirrors the real wire shape: anonymous object -> Web defaults => camelCase keys, exactly as
    // OutboxEventPublisher emits. JsonSerializerDefaults.Web also turns on case-insensitive reads.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid LostItemId = Guid.NewGuid();
    private static readonly Guid FoundItemId = Guid.NewGuid();
    private static readonly DateTime PostedAt = new(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc);

    private const string LostCreatedTopic = "items.lost_item.created";
    private const string LostUpdatedTopic = "items.lost_item.updated";
    private const string FoundCreatedTopic = "items.found_item.created";
    private const string FoundUpdatedTopic = "items.found_item.updated";

    private static string EventJson(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static ListingEvent? Parse(string topic, object payload) =>
        ListingEventParser.Parse(topic, EventJson(payload));

    // xUnit's Assert.NotNull is void, so under Nullable enable it does not tell the compiler the
    // value is non-null. This asserts, then hands back a non-null ListingEvent for the assertions.
    private static ListingEvent ParsedOrFail(ListingEvent? listingEvent)
    {
        Assert.NotNull(listingEvent);
        return listingEvent!;
    }

    // ---- valid mapping per topic direction ----

    [Fact] // LF-80 AC: a lost-item report entering the bus yields a LOST listing, stamped with its creation time.
    public void Parse_LostItemCreated_ProducesLostListingEventWithCreatedAt()
    {
        var listingEvent = ParsedOrFail(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));

        Assert.Equal(EventId, listingEvent.EventId);
        Assert.Equal(LostCreatedTopic, listingEvent.Topic);
        Assert.Equal(LostItemId, listingEvent.ListingId);
        Assert.Equal(ListingType.Lost, listingEvent.ListingType);
        Assert.Equal(UserId, listingEvent.UserId);
        Assert.Equal(PostedAt, listingEvent.PostedAt);
    }

    [Fact]
    public void Parse_FoundItemCreated_ProducesFoundListingEventWithCreatedAt()
    {
        var listingEvent = ParsedOrFail(Parse(FoundCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            foundItemId = FoundItemId,
            createdAt = PostedAt
        }));

        Assert.Equal(FoundItemId, listingEvent.ListingId);
        Assert.Equal(ListingType.Found, listingEvent.ListingType);
        Assert.Equal(PostedAt, listingEvent.PostedAt);
    }

    [Fact] // LF-80 AC: an update to an already-known lost listing keeps the same listing id, stamped with the update time.
    public void Parse_LostItemUpdated_IgnoresMissingCreatedAt_UsesUpdatedAt()
    {
        var updatedAt = new DateTime(2026, 5, 2, 9, 15, 0, DateTimeKind.Utc);

        // The real LostItemUpdatedEvent payload has no createdAt property - only updatedAt.
        var listingEvent = ParsedOrFail(Parse(LostUpdatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            updatedAt
        }));

        Assert.Equal(ListingType.Lost, listingEvent.ListingType);
        Assert.Equal(LostItemId, listingEvent.ListingId);
        Assert.Equal(updatedAt, listingEvent.PostedAt);
    }

    [Fact]
    public void Parse_FoundItemUpdated_IgnoresMissingCreatedAt_UsesUpdatedAt()
    {
        var updatedAt = new DateTime(2026, 5, 2, 9, 15, 0, DateTimeKind.Utc);

        var listingEvent = ParsedOrFail(Parse(FoundUpdatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            foundItemId = FoundItemId,
            updatedAt
        }));

        Assert.Equal(ListingType.Found, listingEvent.ListingType);
        Assert.Equal(FoundItemId, listingEvent.ListingId);
        Assert.Equal(updatedAt, listingEvent.PostedAt);
    }

    [Fact] // Forward compatibility: the full published payload carries many properties the parser does not need.
    public void Parse_FullPublishedWireShape_IgnoresExtraProperties()
    {
        var listingEvent = ParsedOrFail(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            eventType = "lost_item.created",
            timestamp = PostedAt,
            userId = UserId,
            lostItemId = LostItemId,
            title = "Blue backpack",
            category = "Bags",
            description = "Blue bag with a name tag.",
            dateLost = "2026-05-01",
            lastKnownLocation = "Platform 9",
            hiddenInformation = "sticker on the strap",
            status = "OPEN",
            photoUrls = new[] { "https://cdn.example.test/photo-1.jpg" },
            createdAt = PostedAt
        }));

        Assert.Equal(EventId, listingEvent.EventId);
        Assert.Equal(LostItemId, listingEvent.ListingId);
        Assert.Equal(UserId, listingEvent.UserId);
        Assert.Equal(PostedAt, listingEvent.PostedAt);
    }

    // ---- timestamp normalisation ----

    [Fact] // A Z-suffixed ISO timestamp round-trips as Utc unchanged.
    public void Parse_ZuluTimestamp_StaysUtc()
    {
        var listingEvent = ParsedOrFail(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));

        Assert.Equal(DateTimeKind.Utc, listingEvent.PostedAt.Kind);
        Assert.Equal(PostedAt, listingEvent.PostedAt);
    }

    [Fact] // A suffix-free timestamp is Unspecified on the wire and is pinned to Utc as-is (no shift).
    public void Parse_SuffixFreeTimestamp_IsTreatedAsUtcWithoutShifting()
    {
        var unsuffixed = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Unspecified);

        var listingEvent = ParsedOrFail(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = unsuffixed
        }));

        Assert.Equal(DateTimeKind.Utc, listingEvent.PostedAt.Kind);
        Assert.Equal(new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc), listingEvent.PostedAt);
    }

    // ---- malformed input ----

    [Fact]
    public void Parse_NullJson_ReturnsNull() =>
        Assert.Null(ListingEventParser.Parse(LostCreatedTopic, null));

    [Fact]
    public void Parse_WhitespaceJson_ReturnsNull() =>
        Assert.Null(ListingEventParser.Parse(LostCreatedTopic, "   "));

    [Fact]
    public void Parse_MalformedJson_ReturnsNull() =>
        Assert.Null(ListingEventParser.Parse(LostCreatedTopic, "{ this is not json }"));

    [Fact]
    public void Parse_JsonArray_IsNotAnEventPayload_ReturnsNull() =>
        Assert.Null(ListingEventParser.Parse(LostCreatedTopic, "[]"));

    // ---- required-field guards ----

    [Fact]
    public void Parse_EmptyEventId_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = Guid.Empty,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));
    }

    [Fact]
    public void Parse_EmptyUserId_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = Guid.Empty,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));
    }

    [Fact] // A lost_item topic must carry lostItemId; a found-only payload on a lost topic is not a lost event.
    public void Parse_LostTopicWithNoLostItemId_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            foundItemId = FoundItemId,
            createdAt = PostedAt
        }));
    }

    [Fact]
    public void Parse_LostTopicWithEmptyLostItemId_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = Guid.Empty,
            createdAt = PostedAt
        }));
    }

    [Fact]
    public void Parse_FoundTopicWithNoFoundItemId_ReturnsNull()
    {
        Assert.Null(Parse(FoundCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));
    }

    [Fact] // A created topic without createdAt cannot be timestamped.
    public void Parse_CreatedTopicWithoutCreatedAt_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            updatedAt = PostedAt
        }));
    }

    [Fact] // A created topic whose createdAt is the sentinel 0001-01-01 is rejected the same way.
    public void Parse_CreatedTopicWithDefaultCreatedAt_ReturnsNull()
    {
        Assert.Null(Parse(LostCreatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = default(DateTime)
        }));
    }

    [Fact]
    public void Parse_UpdatedTopicWithoutUpdatedAt_ReturnsNull()
    {
        Assert.Null(Parse(LostUpdatedTopic, new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            createdAt = PostedAt
        }));
    }

    // ---- topic-shape fallthrough (design decision pin) ----

    [Fact] // Only topics ending in .created mean "created"; every other suffix falls back to the update path.
    public void Parse_UnrecognizedTopicSuffix_IsTreatedAsAnUpdate()
    {
        // Life-cycle topics such as items.lost_item.resolved are not subscribed, but the parser is
        // deliberately lenient: an unrecognised suffix is interpreted as the update shape.
        var listingEvent = ParsedOrFail(Parse("items.lost_item.resolved", new
        {
            eventId = EventId,
            userId = UserId,
            lostItemId = LostItemId,
            updatedAt = PostedAt
        }));

        Assert.Equal(ListingType.Lost, listingEvent.ListingType);
        Assert.Equal(PostedAt, listingEvent.PostedAt);
    }
}