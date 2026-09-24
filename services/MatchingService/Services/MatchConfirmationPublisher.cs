using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Configuration;
using MatchingService.Matches;
using Microsoft.Extensions.Options;

namespace MatchingService.Services;

public sealed class MatchConfirmationPublisher(
    MatchConfirmationDeliveryStore store,
    IOptions<KafkaSettings> settings,
    IHostEnvironment environment,
    ILogger<MatchConfirmationPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = settings.Value.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 30000
        }).Build();

        var topicReady = !environment.IsDevelopment();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!topicReady)
                    {
                        topicReady = await EnsureDevelopmentTopicAsync();
                        if (!topicReady)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                            continue;
                        }
                    }

                    var delivery = await store.ClaimAsync(stoppingToken);
                    if (delivery is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        continue;
                    }

                    try
                    {
                        await producer.ProduceAsync(delivery.Topic, new Message<string, string>
                        {
                            Key = delivery.MatchId.ToString(),
                            Value = delivery.Payload
                        }, stoppingToken);

                        // A crash before this write may resend the event; Item Service deduplicates it.
                        await store.CompleteAsync(delivery, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            "Match event {EventId} will be retried. Code: MATCH_EVENT_DELIVERY_FAILED. Type: {Type}.",
                            delivery.EventId, exception.GetType().Name);
                        await store.RetryAsync(delivery, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError("Match outbox cycle failed. Code: MATCH_OUTBOX_FAILED. Type: {Type}.",
                        exception.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<bool> EnsureDevelopmentTopicAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = settings.Value.BootstrapServers,
            SocketTimeoutMs = 10000
        }).Build();
        try
        {
            await admin.CreateTopicsAsync([new TopicSpecification
            {
                Name = MatchConfirmationOutbox.Topic,
                NumPartitions = 1,
                ReplicationFactor = 1
            }], new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });
            return true;
        }
        catch (CreateTopicsException exception)
            when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Match topic setup will be retried. Code: MATCH_TOPIC_SETUP_FAILED. Type: {Type}.",
                exception.GetType().Name);
            return false;
        }
    }
}
