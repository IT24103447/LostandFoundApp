using System.Text.Json;
using MatchingService.Models;
using MatchingService.Repositories;

namespace MatchingService.Services;

public sealed class ImageDescriptionEventHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IImageDescriptionRepository _repository;
    private readonly BlobUrlPhotoKeyGenerator _photoKeys;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImageDescriptionEventHandler> _logger;

    public ImageDescriptionEventHandler(
        IImageDescriptionRepository repository,
        BlobUrlPhotoKeyGenerator photoKeys,
        TimeProvider timeProvider,
        ILogger<ImageDescriptionEventHandler> logger)
    {
        _repository = repository;
        _photoKeys = photoKeys;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> HandleAsync(
        string topic,
        string json,
        CancellationToken cancellationToken)
    {
        var itemType = InferItemType(topic);
        var eventType = InferEventType(topic);

        var itemEvent = JsonSerializer.Deserialize<ItemCreatedEvent>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException(
                "The item event payload is empty.");

        if (itemEvent.EventId == Guid.Empty)
        {
            throw new InvalidDataException(
                "The item event has no event ID.");
        }

        if (itemEvent.Timestamp == default)
        {
            throw new InvalidDataException(
                "The item event has no timestamp.");
        }

        var itemId = itemEvent.GetItemId(itemType);
        var sourceOccurredAt = itemEvent.Timestamp.ToUniversalTime();
        var insertedCount = 0;

        foreach (var photoUrl in (itemEvent.PhotoUrls ?? [])
                     .Where(url => !string.IsNullOrWhiteSpace(url))
                     .Distinct(StringComparer.Ordinal))
        {
            string normalizedUrl;
            string photoKey;

            try
            {
                (normalizedUrl, photoKey) =
                    _photoKeys.Create(photoUrl);
            }
            catch (InvalidDataException)
            {
                // Old local-development events can contain relative paths.
                // They cannot be sent to Gemini as public Blob URLs.
                _logger.LogWarning(
                    """
                    Skipping a photo from event {EventId}.
                    Error code: {ErrorCode}.
                    """,
                    itemEvent.EventId,
                    "PHOTO_URL_NOT_ABSOLUTE");

                continue;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;

            var record = new ImageDescriptionRecord(
                Guid.NewGuid(),
                itemEvent.EventId,
                eventType,
                sourceOccurredAt,
                photoKey,
                itemId,
                itemType,
                normalizedUrl,
                ImageProcessingStatus.Pending,
                0,
                now,
                now,
                now);

            if (await _repository.CreatePendingAsync(
                    record,
                    cancellationToken))
            {
                insertedCount++;
            }
        }

        _logger.LogInformation(
            """
            Persisted {Count} new image-description job(s)
            for {ItemType} item {ItemId}.
            """,
            insertedCount,
            itemType,
            itemId);

        return insertedCount;
    }

    private static ItemType InferItemType(string topic)
    {
        if (topic.Contains(
                ".lost_item.",
                StringComparison.Ordinal))
        {
            return ItemType.Lost;
        }

        if (topic.Contains(
                ".found_item.",
                StringComparison.Ordinal))
        {
            return ItemType.Found;
        }

        throw new InvalidDataException(
            $"Unsupported item topic '{topic}'.");
    }

    private static ItemEventType InferEventType(string topic)
    {
        if (topic.EndsWith(
                ".created",
                StringComparison.Ordinal))
        {
            return ItemEventType.Created;
        }

        if (topic.EndsWith(
                ".updated",
                StringComparison.Ordinal))
        {
            return ItemEventType.Updated;
        }

        throw new InvalidDataException(
            $"Unsupported item event topic '{topic}'.");
    }
}