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

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        _consumer.Subscribe(
        [
            _settings.LostItemCreatedTopic,
            _settings.FoundItemCreatedTopic,
            _settings.LostItemUpdatedTopic,
            _settings.FoundItemUpdatedTopic
        ]);

        _logger.LogInformation(
            "Listening for created and updated lost/found item events.");

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
                    when (exception.Error.Code ==
                          ErrorCode.UnknownTopicOrPart)
                {
                    _logger.LogWarning(
                        """
                        A subscribed Kafka topic does not exist yet.
                        Create all configured item topics.
                        """);

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);

                    continue;
                }
                catch (ConsumeException exception)
                {
                    _logger.LogError(
                        """
                        Kafka consumption failed.
                        Code: {ErrorCode}.
                        """,
                        exception.Error.Code);

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);

                    continue;
                }

                try
                {
                    if (result.Message?.Value is null)
                    {
                        throw new InvalidDataException(
                            "The Kafka message body is empty.");
                    }

                    await _handler.HandleAsync(
                        result.Topic,
                        result.Message.Value,
                        stoppingToken);

                    _consumer.Commit(result);
                }
                catch (InvalidDataException exception)
                {
                    _logger.LogError(
                        exception,
                        "Discarding malformed event at {Position}.",
                        result.TopicPartitionOffset);

                    _consumer.Commit(result);
                }
                catch (JsonException exception)
                {
                    _logger.LogError(
                        exception,
                        "Discarding unreadable event at {Position}.",
                        result.TopicPartitionOffset);

                    _consumer.Commit(result);
                }
                catch (Exception exception)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        """
                        Could not persist item event at {Position}.
                        Exception type: {ExceptionType}.
                        The event will be retried.
                        """,
                        result.TopicPartitionOffset,
                        exception.GetType().FullName);

                    _consumer.Seek(result.TopicPartitionOffset);

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _consumer.Close();
        }
    }
}