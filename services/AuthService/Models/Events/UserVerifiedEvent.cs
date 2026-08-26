namespace AuthService.Models.Events;

public sealed class UserVerifiedEvent : BaseEvent
{
    public override string EventType => "user.verified";
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
