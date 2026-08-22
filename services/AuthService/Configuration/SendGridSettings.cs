namespace AuthService.Configuration;

public class SendGridSettings
{
    /// <summary>
    /// ECDSA public key used to verify the signature on incoming SendGrid Event Webhook POSTs.
    /// Get this from SendGrid Dashboard → Settings → Mail Settings → Webhooks → "Signed Event Webhook Requests".
    ///</summary>
    public string WebhookPublicKey { get; set; } = string.Empty;
}
