using AuthService.Repositories;
using AuthService.Webhooks;

namespace AuthService.Services;

public class SendGridEmailBounceService : IEmailBounceService
{
    private static readonly HashSet<string> BounceEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "bounce",
        "dropped",
        "spamreport",
        "blocked",
    };

    private readonly IEmailBouncesRepository _bounces;
    private readonly IEmailVerificationTokensRepository _tokens;
    private readonly IUsersRepository _users;
    private readonly ILogger<SendGridEmailBounceService> _logger;

    public SendGridEmailBounceService(
        IEmailBouncesRepository bounces,
        IEmailVerificationTokensRepository tokens,
        IUsersRepository users,
        ILogger<SendGridEmailBounceService> logger)
    {
        _bounces = bounces;
        _tokens = tokens;
        _users = users;
        _logger = logger;
    }

    public async Task HandleAsync(SendGridEvent evt, string? rawPayloadJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.Event) || string.IsNullOrWhiteSpace(evt.Email))
        {
            _logger.LogWarning("SendGrid event missing event/email: {Raw}", rawPayloadJson);
            return;
        }

        var occurredAt = DateTimeOffset.FromUnixTimeSeconds(evt.Timestamp).UtcDateTime;

        // Look up the user record so we can correlate the bounce to a user.
        var user = await _users.GetByEmailAsync(evt.Email, ct);
        var userId = user?.Id;

        // Always audit-log the bounce.
        await _bounces.RecordAsync(
            userId: userId,
            email: evt.Email,
            eventType: evt.Event,
            reason: evt.Reason,
            sgMessageId: evt.SgMessageId,
            occurredAt: occurredAt,
            rawPayloadJson: rawPayloadJson,
            ct: ct);

        if (!BounceEvents.Contains(evt.Event))
        {
            return;
        }

        // Mark the latest active verification token for this email as bounced.
        var affected = await _tokens.MarkLatestBouncedForEmailAsync(evt.Email, ct);
        if (userId.HasValue && affected > 0)
        {
            await _users.SetEmailBouncedAsync(userId.Value, ct);
            _logger.LogWarning(
                "Email bounce recorded for user {UserId} ({Email}): {EventType} {Reason}",
                userId, evt.Email, evt.Event, evt.Reason);
        }
        else if (userId.HasValue)
        {
            _logger.LogInformation(
                "Email bounce for user {UserId} ({Email}): {EventType} (no active token to invalidate)",
                userId, evt.Email, evt.Event);
        }
        else
        {
            _logger.LogInformation(
                "Email bounce for unknown recipient {Email}: {EventType}",
                evt.Email, evt.Event);
        }
    }
}
