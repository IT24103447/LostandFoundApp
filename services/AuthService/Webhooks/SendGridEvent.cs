using System.Text.Json.Serialization;

namespace AuthService.Webhooks;

/// <summary>
/// SendGrid Event Webhook payload. SendGrid POSTs an ARRAY of these events.
///</summary>
public class SendGridEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("sg_message_id")]
    public string? SgMessageId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("smtp-id")]
    public string? SmtpId { get; set; }
}
