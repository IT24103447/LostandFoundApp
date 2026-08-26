namespace AuthService.Models.Events;

public sealed class UserKickedEvent : BaseEvent
{
    public override string EventType => "user.kicked";
    public Guid KickedBy { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
