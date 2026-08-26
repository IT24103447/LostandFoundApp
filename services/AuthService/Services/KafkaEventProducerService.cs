using AuthService.Configuration;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace AuthService.Services;

public sealed class KafkaEventProducerService : BackgroundService
{
    private readonly KafkaEventPublisher _publisher;
    private readonly KafkaSettings _settings;
    private readonly ILogger<KafkaEventProducerService> _logger;

    public KafkaEventProducerService(
        KafkaEventPublisher publisher,
        IOptions<KafkaSettings> settings,
        ILogger<KafkaEventProducerService> logger)
    {
        _publisher = publisher;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = _settings.BootstrapServers,
            EnableIdempotence = false,
            Acks = Acks.All,
            MessageSendMaxRetries = 3,
            RetryBackoffMs = 500
        };

        using var producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) =>
                _logger.LogWarning("Kafka producer error: {Reason}", e.Reason))
            .SetLogHandler((_, log) =>
                _logger.LogDebug("Kafka: {Message}", log.Message))
            .Build();

        _logger.LogInformation("Kafka event producer started. Listening for events...");

        await foreach (var outbound in _publisher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var message = new Message<string, string>
                {
                    Key = Guid.NewGuid().ToString(),
                    Value = outbound.JsonPayload
                };

                var result = await producer.ProduceAsync(outbound.Topic, message, stoppingToken);

                _logger.LogDebug("Published event to {Topic} (partition {Partition}, offset {Offset}).",
                    result.Topic, result.Partition, result.Offset);
            }
            catch (ProduceException<string, string> ex)
            {
                _logger.LogWarning(ex, "Failed to produce event to {Topic}: {Reason}",
                    outbound.Topic, ex.Error.Reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error producing event to {Topic}.", outbound.Topic);
            }
        }

        _logger.LogInformation("Kafka event producer stopped.");
    }
}
