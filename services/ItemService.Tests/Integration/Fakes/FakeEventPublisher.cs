using System.Collections.Concurrent;
using System.Text.Json;
using ItemService.Services;

namespace ItemService.Tests.Integration.Fakes;

public record PublishedEvent(string Topic, string JsonPayload);

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
