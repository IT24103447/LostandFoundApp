namespace AuthService.Models.Events;

public abstract class BaseEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public abstract string EventType { get; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Guid UserId { get; init; }
}
