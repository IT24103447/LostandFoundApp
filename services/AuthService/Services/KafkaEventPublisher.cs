using System.Text.Json;
using System.Threading.Channels;

namespace AuthService.Services;

internal record OutboundEvent(string Topic, string JsonPayload);

public sealed class KafkaEventPublisher : IEventPublisher
{
    private readonly Channel<OutboundEvent> _channel;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<OutboundEvent>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
    }

    public ValueTask PublishAsync<T>(string topic, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        var outbound = new OutboundEvent(topic, json);

        if (!_channel.Writer.TryWrite(outbound))
        {
            _logger.LogWarning("Kafka event buffer full. Dropping event {EventType} for topic {Topic}.",
                typeof(T).Name, topic);
            return ValueTask.CompletedTask;
        }

        _logger.LogDebug("Enqueued event {EventType} for topic {Topic}.", typeof(T).Name, topic);
        return ValueTask.CompletedTask;
    }

    internal ChannelReader<OutboundEvent> Reader => _channel.Reader;
}
