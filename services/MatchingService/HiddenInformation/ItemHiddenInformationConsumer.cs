using System.Text.Json;
using Confluent.Kafka;

namespace MatchingService.HiddenInformation;

public sealed class ItemHiddenInformationConsumer(
    ItemHiddenInformationRepository repository,
    IConfiguration configuration,
    ILogger<ItemHiddenInformationConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var servers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
        var prefix = configuration["Kafka:TopicPrefix"] ?? "items";

        string[] topics =
        [
            $"{prefix}.lost_item.created",
            $"{prefix}.lost_item.updated",
            $"{prefix}.found_item.created",
            $"{prefix}.found_item.updated"
        ];

        try
        {
            using var consumer = new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = servers,
                    GroupId = configuration["Kafka:HiddenInformationGroupId"]
                        ?? "matching-service-hidden-information",
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnableAutoCommit = false,
                    EnableAutoOffsetStore = false
                }).Build();

            consumer.Subscribe(topics);

            try
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
                            "Hidden information consumption failed. Kafka code: {Code}.",
                            exception.Error.Code);

                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
                            "Hidden information event will retry at {Position}. Type: {Type}.",
                            result.TopicPartitionOffset, exception.GetType().Name);

                        try
                        {
                            consumer.Seek(result.TopicPartitionOffset);
                        }
                        catch (KafkaException)
                        {
                            consumer.Unsubscribe();
                            consumer.Subscribe(topics);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
            }
            finally
            {
                consumer.Close();
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(
        ConsumeResult<string, string> result,
        CancellationToken cancellationToken)
    {
        var itemType = result.Topic.Contains(".lost_item.", StringComparison.Ordinal)
            ? "LOST"
            : "FOUND";

        EventPayload? payload;

        try
        {
            payload = string.IsNullOrWhiteSpace(result.Message?.Value)
                ? null
                : JsonSerializer.Deserialize<EventPayload>(result.Message.Value, JsonOptions);
        }
        catch (JsonException)
        {
            payload = null;
        }

        var itemId = itemType == "LOST" ? payload?.LostItemId : payload?.FoundItemId;

        if (payload is null ||
            itemId is null ||
            itemId == Guid.Empty ||
            payload.Timestamp == default ||
            string.IsNullOrWhiteSpace(payload.HiddenInformation))
        {
            logger.LogWarning(
                "Skipped hidden information event at {Position}. Code: INVALID_ITEM_EVENT.",
                result.TopicPartitionOffset);
            return;
        }

        await repository.SaveAsync(
            itemType,
            itemId.Value,
            payload.HiddenInformation.Trim(),
            payload.Timestamp.UtcDateTime,
            cancellationToken);
    }

    private sealed class EventPayload
    {
        public DateTimeOffset Timestamp { get; init; }
        public Guid? LostItemId { get; init; }
        public Guid? FoundItemId { get; init; }
        public string? HiddenInformation { get; init; }
    }
}
