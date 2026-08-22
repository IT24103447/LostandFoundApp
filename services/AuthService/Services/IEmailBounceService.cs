using AuthService.Webhooks;

namespace AuthService.Services;

public interface IEmailBounceService
{
    Task HandleAsync(SendGridEvent evt, string? rawPayloadJson, CancellationToken ct = default);
}
