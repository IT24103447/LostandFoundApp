using System.Text.Json;
using MatchingService.Configuration;
using MatchingService.Models;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace MatchingService.UnitTests.PhotoDescriptionPipeline;

public class PhotoDescriptionPipelineTests
{
    private static readonly DateTime EventTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string PhotoUrl = "https://photos.example.com/items/blue-bag.jpg";

    [Theory]
    [InlineData("items.lost_item.created", "LOST", "CREATED")]
    [InlineData("items.found_item.created", "FOUND", "CREATED")]
    [InlineData("items.lost_item.updated", "LOST", "UPDATED")]
    [InlineData("items.found_item.updated", "FOUND", "UPDATED")]
    public async Task ItemEvents_QueueTheCorrectPhotoAsPendingWithoutCallingAi(
        string topic, string expectedType, string expectedEventType)
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        ImageDescriptionRecord? queued = null;
        repository.Setup(x => x.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) => queued = record)
            .ReturnsAsync(true);

        var added = await Handler(repository).HandleAsync(topic, EventJson(itemId, eventId, PhotoUrl), CancellationToken.None);

        Assert.Equal(1, added);
        Assert.NotNull(queued);
        Assert.Equal(itemId, queued!.ItemId);
        Assert.Equal(eventId, queued.SourceEventId);
        Assert.Equal(expectedType, queued.ItemType.ToString().ToUpperInvariant());
        Assert.Equal(expectedEventType, queued.SourceEventType.ToString().ToUpperInvariant());
        Assert.Equal(PhotoUrl, queued.BlobUrl);
        Assert.Equal(EventTime, queued.SourceOccurredAt);
        Assert.Equal(ImageProcessingStatus.Pending, queued.ProcessingStatus);
        Assert.Equal(0, queued.Attempts);
        Assert.NotEqual(Guid.Empty, queued.Id);
        repository.VerifyAll();
    }

    [Fact]
    public async Task RedeliveredPhotoEvent_ReusesTheSamePhotoKeyAndQueuesNoSecondDescription()
    {
        var itemId = Guid.NewGuid();
        var json = EventJson(itemId, Guid.NewGuid(), PhotoUrl);
        var knownKeys = new HashSet<string>(StringComparer.Ordinal);
        var seen = new List<ImageDescriptionRecord>();
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        repository.Setup(x => x.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ImageDescriptionRecord record, CancellationToken _) =>
            {
                seen.Add(record);
                return knownKeys.Add(record.PhotoKey);
            });
        var handler = Handler(repository);

        Assert.Equal(1, await handler.HandleAsync("items.lost_item.created", json, CancellationToken.None));
        Assert.Equal(0, await handler.HandleAsync("items.lost_item.created", json, CancellationToken.None));

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0].PhotoKey, seen[1].PhotoKey);
        Assert.Single(knownKeys);
        repository.VerifyAll();
    }

    [Fact]
    public async Task RepeatedUrlInsideOneEvent_IsQueuedOnlyOnce()
    {
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        repository.Setup(x => x.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var json = EventJson(Guid.NewGuid(), Guid.NewGuid(), PhotoUrl, PhotoUrl);

        Assert.Equal(1, await Handler(repository).HandleAsync("items.found_item.created", json, CancellationToken.None));
        repository.Verify(x => x.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventWithoutPhotos_CreatesNoAnalysisJob()
    {
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        var json = EventJson(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(0, await Handler(repository).HandleAsync("items.lost_item.created", json, CancellationToken.None));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RelativePhotoPath_IsNotQueuedForExternalAiAnalysis()
    {
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        var json = EventJson(Guid.NewGuid(), Guid.NewGuid(), "/local/photos/bag.jpg");

        Assert.Equal(0, await Handler(repository).HandleAsync("items.lost_item.created", json, CancellationToken.None));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EventWithoutId_IsRejectedBeforeAnyDescriptionIsStored()
    {
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        var json = EventJson(Guid.NewGuid(), Guid.Empty, PhotoUrl);

        await Assert.ThrowsAsync<InvalidDataException>(() => Handler(repository).HandleAsync(
            "items.found_item.created", json, CancellationToken.None));
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("items.lost_item.resolved")]
    [InlineData("items.other_item.created")]
    public async Task UnsupportedEvent_IsRejectedBeforeQueuing(string topic)
    {
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidDataException>(() => Handler(repository).HandleAsync(
            topic, EventJson(Guid.NewGuid(), Guid.NewGuid(), PhotoUrl), CancellationToken.None));
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("0771234567")]
    [InlineData("https://private.example.com")]
    public void GeneratedDescription_RejectsPrivateContactDetails(string privateText)
    {
        var validator = new ImageDescriptionValidator();
        var json = ValidDescription($"A blue canvas backpack with {privateText} visible.");

        var error = Assert.Throws<ImageProcessingException>(() => validator.Validate(json));
        Assert.Equal("AI_OUTPUT_PRIVACY_REJECTED", error.Code);
    }

    [Fact]
    public void GeneratedDescription_PreservesVisibleAttributesForMatching()
    {
        var result = new ImageDescriptionValidator().Validate(
            ValidDescription("A blue canvas backpack with a striped front pocket."));

        Assert.Equal("backpack", result.ObjectType);
        Assert.Equal(new[] { "blue" }, result.Colours);
        Assert.Equal(new[] { "canvas" }, result.Materials);
        Assert.Equal(new[] { "striped" }, result.Patterns);
    }

    private static ImageDescriptionEventHandler Handler(Mock<IImageDescriptionRepository> repository) => new(
        repository.Object, new BlobUrlPhotoKeyGenerator(), TimeProvider.System,
        NullLogger<ImageDescriptionEventHandler>.Instance);

    private static string EventJson(Guid itemId, Guid eventId, params string[] urls) =>
        JsonSerializer.Serialize(new
        {
            eventId,
            timestamp = EventTime,
            lostItemId = itemId,
            foundItemId = itemId,
            photoUrls = urls
        });

    private static string ValidDescription(string description) => JsonSerializer.Serialize(new
    {
        description,
        objectType = "backpack",
        colours = new[] { "blue" },
        materials = new[] { "canvas" },
        visibleBrand = (string?)null,
        patterns = new[] { "striped" },
        condition = "used",
        distinctiveFeatures = new[] { "front pocket" },
        uncertainty = Array.Empty<string>()
    });
}
