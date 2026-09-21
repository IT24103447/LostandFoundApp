using MatchingService.Models;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MatchingService.Tests.Services;

/// <summary>
/// Story 1, Scenario 10 (photo replacement -> superseded description) contract tests for the
/// updated-event side of ImageDescriptionEventHandler, and the second clause of Scenario 2
/// ("or an updated event when a photo is replaced"). Item Service publishes a lost_item.updated or
/// found_item.updated event carrying the full current photo list whenever a report is edited or its
/// photo is replaced. The handler must turn a new photo URL into a pending row tagged as an update,
/// stamped with the event's own timestamp, so the repository can later supersede the prior description.
/// </summary>
public sealed class ImageDescriptionEventHandlerPhotoReplacementTests
{
    private static readonly Guid EventId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid LostItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid FoundItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTime EventTimestamp = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);

    private const string OldPhotoUrl = "https://blob.example.com/old-photo.jpg";
    private const string NewPhotoUrl = "https://blob.example.com/new-photo.jpg";

    private static ImageDescriptionEventHandler BuildHandler(
        Mock<IImageDescriptionRepository> repository) =>
        new(
            repository.Object,
            new BlobUrlPhotoKeyGenerator(),
            TimeProvider.System,
            Mock.Of<ILogger<ImageDescriptionEventHandler>>());

    private static string LostUpdatedJson(string photoUrlsJson, string? timestamp = null)
    {
        var eventTimestamp = timestamp ?? EventTimestamp.ToString("O");

        return $$"""
            {
              "eventId": "{{EventId}}",
              "timestamp": "{{eventTimestamp}}",
              "lostItemId": "{{LostItemId}}",
              "foundItemId": null,
              "photoUrls": {{photoUrlsJson}}
            }
            """;
    }

    private static string FoundUpdatedJson(string photoUrlsJson) =>
        $$"""
        {
          "eventId": "{{EventId}}",
          "timestamp": "{{EventTimestamp:O}}",
          "lostItemId": null,
          "foundItemId": "{{FoundItemId}}",
          "photoUrls": {{photoUrlsJson}}
        }
        """;

    // Scenario 10: a lost_item.updated event with a new photo URL becomes a pending row tagged as an update.
    [Fact]
    public async Task HandleAsync_LostItemUpdatedTopic_TagsRecordAsUpdatedWithEventTimestamp()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        ImageDescriptionRecord? captured = null;
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.updated",
            LostUpdatedJson($"[\"{NewPhotoUrl}\"]"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        Assert.NotNull(captured);
        Assert.Equal(ItemEventType.Updated, captured!.SourceEventType);
        Assert.Equal(ItemType.Lost, captured.ItemType);
        Assert.Equal(LostItemId, captured.ItemId);
        Assert.Equal(EventTimestamp, captured.SourceOccurredAt);
        Assert.Equal(NewPhotoUrl, captured.BlobUrl);
    }

    // Scenario 10: the same holds for a found_item.updated event, keyed off FoundItemId.
    [Fact]
    public async Task HandleAsync_FoundItemUpdatedTopic_TagsRecordAsUpdatedWithEventTimestamp()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        ImageDescriptionRecord? captured = null;
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.found_item.updated",
            FoundUpdatedJson($"[\"{NewPhotoUrl}\"]"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        Assert.NotNull(captured);
        Assert.Equal(ItemEventType.Updated, captured!.SourceEventType);
        Assert.Equal(ItemType.Found, captured.ItemType);
        Assert.Equal(FoundItemId, captured.ItemId);
        Assert.Equal(EventTimestamp, captured.SourceOccurredAt);
    }

    /* An update event carries the full current photo list, so it can repeat a photo that is already known.
       The known URL hits the unique photo_key and is reported as not inserted. Only the genuinely new photo counts. */
    [Fact]
    public async Task HandleAsync_UpdatedEventRepeatingKnownPhotoAndAddingNewOne_OnlyNewPhotoCountsAsInserted()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        repository
            .Setup(r => r.CreatePendingAsync(
                It.Is<ImageDescriptionRecord>(record => record.BlobUrl == OldPhotoUrl), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repository
            .Setup(r => r.CreatePendingAsync(
                It.Is<ImageDescriptionRecord>(record => record.BlobUrl == NewPhotoUrl), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.updated",
            LostUpdatedJson($"[\"{OldPhotoUrl}\",\"{NewPhotoUrl}\"]"),
            CancellationToken.None);

        Assert.Equal(1, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // An update that does not touch the photo (for example a title edit) may carry a null photo list. That is valid and creates nothing.
    [Fact]
    public async Task HandleAsync_UpdatedEventWithNullPhotoUrls_ReturnsZeroAndNeverCallsRepository()
    {
        var repository = new Mock<IImageDescriptionRepository>();

        var inserted = await BuildHandler(repository).HandleAsync(
            "items.lost_item.updated",
            LostUpdatedJson("null"),
            CancellationToken.None);

        Assert.Equal(0, inserted);
        repository.Verify(
            r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /* The timestamp decides which description is newer, so it must be normalised to UTC.
       An offset timestamp of 15:30 at +05:30 is 10:00 UTC. */
    [Fact]
    public async Task HandleAsync_UpdatedEventWithOffsetTimestamp_StoresSourceOccurredAtInUtc()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        ImageDescriptionRecord? captured = null;
        repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => captured = record)
            .ReturnsAsync(true);

        await BuildHandler(repository).HandleAsync(
            "items.lost_item.updated",
            LostUpdatedJson($"[\"{NewPhotoUrl}\"]", timestamp: "2026-09-21T15:30:00+05:30"),
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(DateTimeKind.Utc, captured!.SourceOccurredAt.Kind);
        Assert.Equal(EventTimestamp, captured.SourceOccurredAt);
    }
}
