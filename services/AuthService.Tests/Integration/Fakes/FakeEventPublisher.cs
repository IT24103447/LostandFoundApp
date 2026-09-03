using System.Collections.Concurrent;
using System.Text.Json;
using AuthService.Services;

namespace AuthService.Tests.Integration.Fakes;

public record PublishedEvent(string Topic, string JsonPayload);

/// <summary>
/// Test double for IEventPublisher. Registered instead of KafkaEventPublisher so
/// integration tests don't need a real Kafka broker. We only care that the app
/// *attempted* to publish the right event with the right shape — not that Kafka
/// itself works (that's Kafka's job to test, not ours).
/// </summary>
public class FakeEventPublisher : IEventPublisher
{
    private readonly ConcurrentBag<PublishedEvent> _published = new();

    public ValueTask PublishAsync<T>(string topic, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        _published.Add(new PublishedEvent(topic, json));
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<PublishedEvent> Published => _published.ToList();

    public bool WasPublishedTo(string topicSuffix) =>
        _published.Any(e => e.Topic.EndsWith(topicSuffix, StringComparison.Ordinal));

    public void Clear() => _published.Clear();
}
