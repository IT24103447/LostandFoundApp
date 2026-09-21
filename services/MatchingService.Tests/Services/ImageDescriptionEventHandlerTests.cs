using MatchingService.Models;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MatchingService.Tests.Services;

/// <summary>Story 1 (photo upload -> AI description) contract tests for the Kafka-consumed event handler.</summary>
public sealed class ImageDescriptionEventHandlerTests
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LostItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FoundItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime EventTimestamp = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);

    private static ImageDescriptionEventHandler BuildHandler(
        Mock<IImageDescriptionRepository> repository) =>
        new(
            repository.Object,
            new BlobUrlPhotoKeyGenerator(),
            TimeProvider.System,
            Mock.Of<ILogger<ImageDescriptionEventHandler>>());

    private static string LostEventJson(params string[] photoUrls) =>
        $$"""
        {
          "eventId": "{{EventId}}",
          "timestamp": "{{EventTimestamp:O}}",
          "lostItemId": "{{LostItemId}}",
          "foundItemId": null,
          "photoUrls": [{{string.Join(",", photoUrls.Select(u => $"\"{u}\""))}}]
        }
        """;

    private static string FoundEventJson(params string[] photoUrls) =>
        $$"""
        {
          "eventId": "{{EventId}}",
          "timestamp": "{{EventTimestamp:O}}",
          "lostItemId": null,
          "foundItemId": "{{FoundItemId}}",
          "photoUrls": [{{string.Join(",", photoUrls.Select(u => $"\"{u}\""))}}]
        }
        """;

    // Scenario 5: a lost_item.created topic is resolved to ItemType.Lost and keyed off LostItemId.
    [Fact]
    public async Task HandleAsync_LostItemCreatedTopic_InfersLostTypeAndPersistsAgainstLostItemId()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        ImageDescriptionRecord? captured = null;
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson("https://blob.example.com/a.jpg"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        Assert.NotNull(captured);
        Assert.Equal(ItemType.Lost, captured!.ItemType);
        Assert.Equal(LostItemId, captured.ItemId);
        Assert.Equal(ItemEventType.Created, captured.SourceEventType);
        Assert.Equal(EventTimestamp, captured.SourceOccurredAt);
    }

    /* Scenario 5: a found_item.created topic is resolved to ItemType.Found and keyed off FoundItemId,
       confirming both item types are processed by the same handler code path. */
    [Fact]
    public async Task HandleAsync_FoundItemCreatedTopic_InfersFoundTypeAndPersistsAgainstFoundItemId()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        ImageDescriptionRecord? captured = null;
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.found_item.created",
            FoundEventJson("https://blob.example.com/b.jpg"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        Assert.NotNull(captured);
        Assert.Equal(ItemType.Found, captured!.ItemType);
        Assert.Equal(FoundItemId, captured.ItemId);
        Assert.Equal(ItemEventType.Created, captured.SourceEventType);
        Assert.Equal(EventTimestamp, captured.SourceOccurredAt);
    }

    /* A topic that isn't a recognised created or updated topic (here a resolved event, which the
       consumer never subscribes to) must fail loudly rather than be silently
       ignored, matching ItemCreatedEventConsumer's malformed-message handling (logged and the offset still committed) */
    [Fact]
    public async Task HandleAsync_UnsupportedTopic_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync(
                "items.lost_item.resolved",
                LostEventJson("https://blob.example.com/a.jpg"),
                CancellationToken.None));
    }

    // A topic whose item type is neither lost nor found must fail loudly too, before any JSON is read.
    [Fact]
    public async Task HandleAsync_UnknownItemTypeTopic_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync(
                "items.other_item.created",
                LostEventJson("https://blob.example.com/a.jpg"),
                CancellationToken.None));
    }

    /* A JSON body of the literal `null` deserializes successfully but yields no object,
       which must be rejected explicitly rather than proceeding with a null event. */
    [Fact]
    public async Task HandleAsync_NullPayload_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync(
                "items.lost_item.created",
                "null",
                CancellationToken.None));
    }

    /* A syntactically-invalid payload must surface as JsonException so ItemCreatedEventConsumer's
       catch (InvalidDataException or JsonException) branch discards it instead of crashing the consumer. */
    [Fact]
    public async Task HandleAsync_MalformedJson_ThrowsJsonException()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            BuildHandler(repository).HandleAsync(
                "items.lost_item.created",
                "{ not valid json",
                CancellationToken.None));
    }

    /* An empty GUID event ID means the payload wasn't produced correctly upstream. 
       It must be rejected before anything is written, not persisted with a meaningless source_event_id. */
    [Fact]
    public async Task HandleAsync_EmptyEventId_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var json = $$"""
            {"eventId":"00000000-0000-0000-0000-000000000000","timestamp":"{{EventTimestamp:O}}","lostItemId":"{{LostItemId}}","foundItemId":null,"photoUrls":["https://blob.example.com/a.jpg"]}
            """;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync("items.lost_item.created", json, CancellationToken.None));
    }

    /* The event timestamp is what orders a replacement against the photo it replaces, so an event
       without one must be rejected before anything is written. The consumer discards it as malformed. */
    [Fact]
    public async Task HandleAsync_MissingTimestamp_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var json = $$"""
            {"eventId":"{{EventId}}","lostItemId":"{{LostItemId}}","foundItemId":null,"photoUrls":["https://blob.example.com/a.jpg"]}
            """;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync("items.lost_item.created", json, CancellationToken.None));

        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /* GetItemId's own guard: a lost_item.created topic whose payload has no LostItemId is invalid,
       even though it deserializes fine and even though FoundItemId might be absent too. */
    [Fact]
    public async Task HandleAsync_LostTopicMissingLostItemId_ThrowsInvalidDataException()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var json = $$"""
            {"eventId":"{{EventId}}","timestamp":"{{EventTimestamp:O}}","lostItemId":null,"foundItemId":null,"photoUrls":["https://blob.example.com/a.jpg"]}
            """;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BuildHandler(repository).HandleAsync("items.lost_item.created", json, CancellationToken.None));
    }

    // Multiple distinct photo URLs on one report each become their own pending row.
    [Fact]
    public async Task HandleAsync_MultiplePhotoUrls_CreatesOneRecordPerUrl()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson("https://blob.example.com/a.jpg", "https://blob.example.com/b.jpg"),
            CancellationToken.None);

        Assert.Equal(2, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /* The exact same URL appearing twice in one payload is deduped before it ever reaches the repository, 
        rather than relying solely on the database's unique constraint to catch it. */
    [Fact]
    public async Task HandleAsync_DuplicateUrlWithinSamePayload_DedupedBeforeInsert()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson("https://blob.example.com/a.jpg", "https://blob.example.com/a.jpg"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /* A URL that fails BlobUrlPhotoKeyGenerator's own validation (not absolute HTTP/HTTPS) is skipped and logged,
       but must not abort processing of the other, valid URLs in the same event. */
    [Fact]
    public async Task HandleAsync_OneInvalidUrlAmongValidOnes_SkipsInvalidAndPersistsTheRest()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson("not-a-url", "https://blob.example.com/a.jpg"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /* Scenario 8: at-least-once Kafka redelivery of the same photo must not be counted as a new insertion. 
       CreatePendingAsync returning false (the repository's unique-constraint signal) means the row already exists,
       so it's silently excluded from the inserted count, not an error. */
    [Fact]
    public async Task HandleAsync_RepositoryReportsExistingRow_NotCountedAsInserted()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson("https://blob.example.com/a.jpg"),
            CancellationToken.None);

        Assert.Equal(0, inserted);
    }

    // No photo URLs at all is a valid (if unusual) event: zero rows created, no exception, no repository call. 
    [Fact]
    public async Task HandleAsync_NoPhotoUrls_ReturnsZeroAndNeverCallsRepository()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.created",
            LostEventJson(),
            CancellationToken.None);

        Assert.Equal(0, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
