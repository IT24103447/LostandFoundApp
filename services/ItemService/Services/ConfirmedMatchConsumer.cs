// changed during sprint 3 by dev
using System.Text.Json;
using Confluent.Kafka;
using ItemService.Configuration;
using ItemService.Models.Events;
using Microsoft.Extensions.Options;

namespace ItemService.Services;

public sealed class ConfirmedMatchConsumer(
    IServiceScopeFactory scopes,
    IOptions<KafkaSettings> settings,
    ILogger<ConfirmedMatchConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = settings.Value.BootstrapServers,
            GroupId = "item-service-match-resolution",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false
        }).Build();
        consumer.Subscribe("matches.confirmed");

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
                    logger.LogWarning("Match event consumption will retry. Kafka code: {Code}.", exception.Error.Code);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                try
                {
                    var message = Decode(result.Message?.Value);
                    if (message is null)
                    {
                        logger.LogError("Invalid match event discarded at {Position}. Code: INVALID_MATCH_EVENT.",
                            result.TopicPartitionOffset);
                    }
                    else
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<ConfirmedMatchHandler>()
                            .HandleAsync(message, stoppingToken);
                    }

                    // Commit only after the database transaction, including its inbox row, has committed.
                    consumer.Commit(result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError("Match event will retry at {Position}. Code: MATCH_RESOLUTION_RETRY. Type: {Type}.",
                        result.TopicPartitionOffset, exception.GetType().Name);
                    try
                    {
                        consumer.Seek(result.TopicPartitionOffset);
                    }
                    catch (KafkaException)
                    {
                        // Rejoin from committed offsets if ownership changed during processing.
                        consumer.Unsubscribe();
                        consumer.Subscribe("matches.confirmed");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            consumer.Close();
        }
    }

    private static MatchConfirmedIntegrationEvent? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var message = JsonSerializer.Deserialize<MatchConfirmedIntegrationEvent>(json, JsonOptions);
            return message?.IsValid() == true ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
