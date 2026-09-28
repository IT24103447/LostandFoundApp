namespace MatchingService.Notifications;

public static class NotificationTypes
{
    public const string CounterpartAction = "COUNTERPART_ACTION";
    public const string MatchConfirmed = "MATCH_CONFIRMED";
    public const string MatchRejected = "MATCH_REJECTED";
}

public sealed record NotificationJob(
    Guid Id,
    Guid MatchId,
    Guid RecipientUserId,
    string Type,
    int Attempts,
    Guid LeaseToken);

public sealed record NotificationRecipient(
    string? Email,
    bool CanSend,
    string? SuppressionCode);

public sealed class NotificationDeliveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IMatchEmailSender
{
    Task SendAsync(
        NotificationJob job,
        string recipientEmail,
        CancellationToken cancellationToken);
}