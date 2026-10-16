using AdminVerifyService.Configuration;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace AdminVerifyService.Listings;

public sealed class ListingEventConsumer(
    ListingEventHandler handler,
    IOptions<KafkaSettings> options,
    ILogger<ListingEventConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var settings = options.Value;

        string[] topics =
        [
            settings.LostItemCreatedTopic,
            settings.FoundItemCreatedTopic,
            settings.LostItemUpdatedTopic,
            settings.FoundItemUpdatedTopic
        ];

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = settings.BootstrapServers,
                    GroupId = settings.GroupId,
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnableAutoCommit = false,
                    EnableAutoOffsetStore = false
                }).Build();

            consumer.Subscribe(topics);

            try
            {
                await ConsumeLoopAsync(consumer, topics, stoppingToken);
            }
            finally
            {
                consumer.Close();
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested.
        }
    }

    private async Task ConsumeLoopAsync(
        IConsumer<string, string> consumer,
        string[] topics,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string> result;

            try
            {
                result = consumer.Consume(stoppingToken);
            }
            catch (ConsumeException exception)
            {
                logger.LogWarning(
                    "Listing event consumption failed. Kafka code: {Code}.",
                    exception.Error.Code);

                await Task.Delay(RetryDelay, stoppingToken);
                continue;
            }

            try
            {
                await HandleAsync(result, stoppingToken);
                consumer.Commit(result);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Listing event will retry at {Position}. Type: {Type}.",
                    result.TopicPartitionOffset, exception.GetType().Name);

                Rewind(consumer, result, topics);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task HandleAsync(
        ConsumeResult<string, string> result,
        CancellationToken cancellationToken)
    {
        var listingEvent = ListingEventParser.Parse(result.Topic, result.Message?.Value);

        if (listingEvent is null)
        {
            logger.LogWarning(
                "Skipped listing event at {Position}. Code: INVALID_ITEM_EVENT.",
                result.TopicPartitionOffset);
            return;
        }

        var outcome = await handler.HandleAsync(listingEvent, cancellationToken);

        logger.LogDebug(
            "Listing event {EventId} for listing {ListingId}: {Outcome}.",
            listingEvent.EventId, listingEvent.ListingId, outcome);
    }

    private static void Rewind(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result,
        string[] topics)
    {
        try
        {
            consumer.Seek(result.TopicPartitionOffset);
        }
        catch (KafkaException)
        {
            consumer.Unsubscribe();
            consumer.Subscribe(topics);
        }
    }
}
