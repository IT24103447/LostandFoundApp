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
        var itemEvent = JsonSerializer.Deserialize<ItemCreatedEvent>(json, JsonOptions)
            ?? throw new InvalidDataException("The item-created event payload is empty.");

        if (itemEvent.EventId == Guid.Empty)
        {
            throw new InvalidDataException("The item-created event has no event ID.");
        }

        var itemId = itemEvent.GetItemId(itemType);
        var insertedCount = 0;

        foreach (var photoUrl in itemEvent.PhotoUrls
                     .Where(url => !string.IsNullOrWhiteSpace(url))
                     .Distinct(StringComparer.Ordinal))
        {
            string normalizedUrl;
            string photoKey;
            try
            {
                (normalizedUrl, photoKey) = _photoKeys.Create(photoUrl);
            }
            catch (InvalidDataException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Ignoring an invalid photo URL in event {EventId} for item {ItemId}.",
                    itemEvent.EventId,
                    itemId);
                continue;
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var record = new ImageDescriptionRecord(
                Guid.NewGuid(),
                itemEvent.EventId,
                photoKey,
                itemId,
                itemType,
                normalizedUrl,
                ImageProcessingStatus.Pending,
                0,
                now,
                now,
                now);

            if (await _repository.CreatePendingAsync(record, cancellationToken))
            {
                insertedCount++;
            }
        }

        _logger.LogInformation(
            "Persisted {Count} new image-description job(s) for {ItemType} item {ItemId}.",
            insertedCount,
            itemType,
            itemId);

        return insertedCount;
    }

    private static ItemType InferItemType(string topic)
    {
        if (topic.EndsWith(".lost_item.created", StringComparison.Ordinal))
        {
            return ItemType.Lost;
        }

        if (topic.EndsWith(".found_item.created", StringComparison.Ordinal))
        {
            return ItemType.Found;
        }

        throw new InvalidDataException($"Unsupported item-created topic '{topic}'.");
    }
}
