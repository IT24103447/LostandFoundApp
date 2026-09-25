using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace MatchingService.Lifecycle;

public sealed class ItemLifecycleConsumer(
    ItemLifecycleRepository repository,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ItemLifecycleConsumer> logger) : BackgroundService
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

        var lostTopic = $"{prefix}.lost_item.resolved";
        var foundTopic = $"{prefix}.found_item.resolved";
        var deleteTopic = $"{prefix}.item.delete_requested";
        string[] topics = [lostTopic, foundTopic, deleteTopic];

        try
        {
            if (environment.IsDevelopment())
            {
                await EnsureTopicsAsync(servers, topics, stoppingToken);
            }

            using var consumer = new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = servers,
                    GroupId = configuration["Kafka:LifecycleGroupId"]
                        ?? "matching-service-item-lifecycle",
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
                            "Lifecycle consumption failed. Kafka code: {Code}.",
                            exception.Error.Code);

                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    try
                    {
                        ItemLifecycleEvent? itemEvent = null;

                        try
                        {
                            itemEvent = Parse(
                                result.Topic, result.Message?.Value,
                                lostTopic, foundTopic, deleteTopic);
                        }
                        catch (Exception exception)
                            when (exception is JsonException or InvalidDataException)
                        {
                            logger.LogError(
                                "Invalid lifecycle event at {Position}. Code: INVALID_ITEM_EVENT.",
                                result.TopicPartitionOffset);
                        }

                        if (itemEvent is not null)
                        {
                            await repository.ApplyAsync(itemEvent, stoppingToken);

                            logger.LogInformation(
                                "Applied {Reason} event {EventId} for {ItemType} item {ItemId}.",
                                itemEvent.Reason, itemEvent.EventId,
                                itemEvent.ItemType, itemEvent.ItemId);
                        }

                        // Commit only after the database transaction succeeds.
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
                            "Lifecycle event will retry at {Position}. Type: {Type}.",
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

    private static ItemLifecycleEvent Parse(
        string topic,
        string? json,
        string lostTopic,
        string foundTopic,
        string deleteTopic)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Empty event.");
        }

        var payload = JsonSerializer.Deserialize<EventPayload>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty event.");

        string type;
        Guid itemId;
        string reason;
        string expectedEventType;

        if (topic == lostTopic)
        {
            type = "LOST";
            itemId = payload.LostItemId;
            reason = "RESOLVED";
            expectedEventType = "lost_item.resolved";
        }
        else if (topic == foundTopic)
        {
            type = "FOUND";
            itemId = payload.FoundItemId;
            reason = "RESOLVED";
            expectedEventType = "found_item.resolved";
        }
        else if (topic == deleteTopic)
        {
            type = payload.ItemType?.Trim().ToUpperInvariant() ?? "";
            itemId = payload.ItemId;
            reason = "DELETED";
            expectedEventType = "item.delete_requested";
        }
        else
        {
            throw new InvalidDataException("Unexpected topic.");
        }

        if (payload.EventId == Guid.Empty ||
            itemId == Guid.Empty ||
            payload.Timestamp == default ||
            type is not ("LOST" or "FOUND") ||
            payload.EventType != expectedEventType)
        {
            throw new InvalidDataException("Invalid event contract.");
        }

        return new ItemLifecycleEvent(
            payload.EventId,
            itemId,
            type,
            reason,
            payload.Timestamp.UtcDateTime);
    }

    private async Task EnsureTopicsAsync(
        string servers,
        string[] topics,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var admin = new AdminClientBuilder(
                    new AdminClientConfig
                    {
                        BootstrapServers = servers,
                        SocketTimeoutMs = 10_000
                    }).Build();

                await admin.CreateTopicsAsync(
                    topics.Select(topic => new TopicSpecification
                    {
                        Name = topic,
                        NumPartitions = 1,
                        ReplicationFactor = 1
                    }),
                    new CreateTopicsOptions
                    {
                        RequestTimeout = TimeSpan.FromSeconds(10)
                    });

                return;
            }
            catch (CreateTopicsException exception)
                when (exception.Results.All(result =>
                    result.Error.Code is ErrorCode.NoError
                        or ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Lifecycle topic setup will retry. Type: {Type}.",
                    exception.GetType().Name);

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private sealed class EventPayload
    {
        public Guid EventId { get; init; }
        public string? EventType { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public Guid LostItemId { get; init; }
        public Guid FoundItemId { get; init; }
        public Guid ItemId { get; init; }
        public string? ItemType { get; init; }
    }
}