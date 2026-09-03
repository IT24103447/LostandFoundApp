namespace AuthService.Models.Events;

public sealed class UserProfileUpdatedEvent : BaseEvent
{
    public override string EventType => "user.profile_updated";
    public string[] UpdatedFields { get; init; } = [];
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
