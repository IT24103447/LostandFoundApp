namespace AuthService.Models.Events;

public sealed class UserUnkickedEvent : BaseEvent
{
    public override string EventType => "user.unkicked";
    public Guid UnkickedBy { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
