using System.Text.Json;
using Confluent.Kafka;
using MatchingService.Configuration;
using Microsoft.Extensions.Options;

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

    protected override Task ExecuteAsync(
        CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken);

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            _consumer.Subscribe(
            [
                _settings.LostItemCreatedTopic,
                _settings.FoundItemCreatedTopic
            ]);

            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;

                try
                {
                    result = _consumer.Consume(stoppingToken);
                }
                catch (ConsumeException)
                {
                    _logger.LogWarning(
                        "Kafka consumption failed: {ErrorCode}.",
                        "KAFKA_CONSUME_FAILED");

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);

                    continue;
                }

                try
                {
                    if (result.Message?.Value is null)
                    {
                        throw new InvalidDataException();
                    }

                    await _handler.HandleAsync(
                        result.Topic,
                        result.Message.Value,
                        stoppingToken);
                }
                catch (Exception exception)
                    when (exception is InvalidDataException or JsonException)
                {
                    _logger.LogWarning(
                        "Malformed event at {Position}: {ErrorCode}.",
                        result.TopicPartitionOffset,
                        "ITEM_EVENT_INVALID");
                }

                // Commit only after persistence, or an explicitly rejected
                // malformed message. Other failures stop the service.
                _consumer.Commit(result);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _logger.LogCritical(
                "Item consumer stopped: {ErrorCode}.",
                "ITEM_CONSUMER_FAILED");

            throw new InvalidOperationException("ITEM_CONSUMER_FAILED");
        }
        finally
        {
            try
            {
                _consumer.Close();
            }
            catch (Exception)
            {
                _logger.LogWarning(
                    "Kafka close failed: {ErrorCode}.",
                    "KAFKA_CLOSE_FAILED");
            }
        }
    }
}