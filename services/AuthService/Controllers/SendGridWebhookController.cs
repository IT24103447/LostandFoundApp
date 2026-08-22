using AuthService.Configuration;
using AuthService.Services;
using AuthService.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AuthService.Controllers;

/// <summary>
/// Receives SendGrid Event Webhook callbacks for email events (bounce, dropped, spamreport, etc).
/// Signature is verified using ECDSA + the configured public key. Endpoint is anonymous because
/// SendGrid signs the request instead of using a shared secret or bearer token.
///</summary>
[ApiController]
[Route("api/webhooks/sendgrid")]
[AllowAnonymous]
public class SendGridWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IEmailBounceService _bounces;
    private readonly SendGridSettings _settings;
    private readonly ILogger<SendGridWebhookController> _logger;

    public SendGridWebhookController(
        IEmailBounceService bounces,
        IOptions<SendGridSettings> settings,
        ILogger<SendGridWebhookController> logger)
    {
        _bounces = bounces;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // Read raw body for signature verification (must be EXACT bytes that SendGrid signed).
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(_settings.WebhookPublicKey))
        {
            _logger.LogWarning("SendGrid webhook received but WebhookPublicKey is not configured.");
            // Still process the events in dev (helps when iterating without a key).
        }
        else
        {
            var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].ToString();
            var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].ToString();
            var ok = SendGridSignatureVerifier.Verify(_settings.WebhookPublicKey, timestamp, signature, rawBody);
            if (!ok)
            {
                _logger.LogWarning("SendGrid webhook signature verification failed.");
                return Unauthorized(new { error = "Invalid signature." });
            }
        }

        SendGridEvent[]? events;
        try
        {
            events = JsonSerializer.Deserialize<SendGridEvent[]>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SendGrid webhook payload parse error.");
            return BadRequest(new { error = "Invalid JSON payload." });
        }

        if (events is null || events.Length == 0)
        {
            return Ok();
        }

        foreach (var evt in events)
        {
            try
            {
                await _bounces.HandleAsync(evt, rawBody, ct);
            }
            catch (Exception ex)
            {
                // Don't fail the whole batch if one event is bad — log and continue.
                _logger.LogError(ex, "Failed to process SendGrid event {Event} for {Email}", evt.Event, evt.Email);
            }
        }

        return Ok();
    }
}
