namespace ItemService.Services;

public interface IEventPublisher
{
    ValueTask PublishAsync<T>(string topic, T payload, CancellationToken ct = default);
}
