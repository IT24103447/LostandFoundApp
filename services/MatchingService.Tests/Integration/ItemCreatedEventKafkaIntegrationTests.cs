using Confluent.Kafka;
using MatchingService.Models;
using MatchingService.Repositories;
using Moq;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 1, Scenario 2 integration test. Proves a real Kafka message on a real, disposable broker is
/// actually consumed by the real ItemCreatedEventConsumer and turned into a real repository call.
/// This is not just proof that the in-process handler logic is correct (see
/// ImageDescriptionEventHandlerTests for that). Requires Docker running locally (Testcontainers.Kafka).
/// </summary>
public sealed class ItemCreatedEventKafkaIntegrationTests : IClassFixture<MatchingServiceKafkaApiFactory>
{
    private readonly MatchingServiceKafkaApiFactory _factory;

    public ItemCreatedEventKafkaIntegrationTests(MatchingServiceKafkaApiFactory factory)
    {
        _factory = factory;
        // Force the host (and its hosted services, including the real consumer) to actually start.
        _ = factory.Server;
    }

    private async Task PublishAsync(string topic, string key, string json)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.GetBootstrapAddress() }).Build();

        await producer.ProduceAsync(topic, new Message<string, string> { Key = key, Value = json });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    /* Scenario 2: a real message on the real lost_item.created topic reaches the real consumer and
       results in a real call into the repository layer, end to end through actual Kafka wire protocol. */
    [Fact]
    public async Task RealLostItemCreatedMessage_IsConsumedAndPersistedAsPendingDescription()
    {
        var eventId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var photoUrl = $"https://blob.example.com/{Guid.NewGuid()}.jpg";

        var tcs = new TaskCompletionSource();
        ImageDescriptionRecord? captured = null;
        _factory.Repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) =>
            {
                if (record.SourceEventId == eventId)
                {
                    captured = record;
                    tcs.TrySetResult();
                }
            })
            .ReturnsAsync(true);

        var json = $$"""
            {"eventId":"{{eventId}}","lostItemId":"{{itemId}}","foundItemId":null,"photoUrls":["{{photoUrl}}"]}
            """;

        await PublishAsync("items.lost_item.created", itemId.ToString(), json);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(captured);
        Assert.Equal(ItemType.Lost, captured!.ItemType);
        Assert.Equal(itemId, captured.ItemId);
        Assert.Equal(photoUrl, captured.BlobUrl);
    }

    /* Scenario 5, via the real broker this time: a found_item.created message is consumed the same
       way and correctly tagged as ItemType.Found. */
    [Fact]
    public async Task RealFoundItemCreatedMessage_IsConsumedAndPersistedAsPendingDescription()
    {
        var eventId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var photoUrl = $"https://blob.example.com/{Guid.NewGuid()}.jpg";

        var tcs = new TaskCompletionSource();
        ImageDescriptionRecord? captured = null;
        _factory.Repository
            .Setup(r => r.CreatePendingAsync(It.IsAny<ImageDescriptionRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ImageDescriptionRecord, CancellationToken>((record, _) =>
            {
                if (record.SourceEventId == eventId)
                {
                    captured = record;
                    tcs.TrySetResult();
                }
            })
            .ReturnsAsync(true);

        var json = $$"""
            {"eventId":"{{eventId}}","lostItemId":null,"foundItemId":"{{itemId}}","photoUrls":["{{photoUrl}}"]}
            """;

        await PublishAsync("items.found_item.created", itemId.ToString(), json);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(captured);
        Assert.Equal(ItemType.Found, captured!.ItemType);
        Assert.Equal(itemId, captured.ItemId);
    }
}
