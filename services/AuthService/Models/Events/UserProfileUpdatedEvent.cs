namespace AuthService.Models.Events;

public sealed class UserProfileUpdatedEvent : BaseEvent
{
    public override string EventType => "user.profile_updated";
    public string[] UpdatedFields { get; init; } = [];
}
