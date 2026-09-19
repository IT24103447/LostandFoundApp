using Confluent.Kafka;
using MatchingService.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MatchingService.Services;

public sealed class ItemCreatedEventConsumer : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly ImageDescriptionEventHandler _handler;
    private readonly KafkaSettings _settings;
    private readonly ILogger<ItemCreatedEventConsumer> _logger;

    public ItemCreatedEventConsumer(
        IConsumer<string, string> consumer,
        ImageDescriptionEventHandler handler,
        IOptions<KafkaSettings> settings,
        ILogger<ItemCreatedEventConsumer> logger)
    {
        _consumer = consumer;
        _handler = handler;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe([
            _settings.LostItemCreatedTopic,
            _settings.FoundItemCreatedTopic
        ]);

        _logger.LogInformation(
            "Listening for item photos on {LostTopic} and {FoundTopic}.",
            _settings.LostItemCreatedTopic,
            _settings.FoundItemCreatedTopic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = _consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    _logger.LogError(exception, "Kafka could not consume an item-created event.");
                    continue;
                }

                try
                {
                    await _handler.HandleAsync(
                        result.Topic,
                        result.Message.Value,
                        stoppingToken);
                    _consumer.Commit(result);
                }
                catch (InvalidDataException exception)
                {
                    // Item events contain hidden verification data, so the payload is never logged.
                    _logger.LogError(
                        exception,
                        "Discarding malformed item event at {TopicPartitionOffset}.",
                        result.TopicPartitionOffset);
                    _consumer.Commit(result);
                }
                catch (JsonException exception)
                {
                    _logger.LogError(
                        exception,
                        "Discarding unreadable item event at {TopicPartitionOffset}.",
                        result.TopicPartitionOffset);
                    _consumer.Commit(result);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Kafka delivery is at least once. Rewind on transient persistence failures;
                    // the unique photo_key makes any already-written URLs safe to receive again.
                    _logger.LogError(
                        exception,
                        "Could not persist item event at {TopicPartitionOffset}; retrying.",
                        result.TopicPartitionOffset);
                    _consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            _consumer.Close();
        }
    }
}
