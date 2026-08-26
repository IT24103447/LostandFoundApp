namespace AuthService.Models.Events;

public sealed class UserDeletedEvent : BaseEvent
{
    public override string EventType => "user.deleted";
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
