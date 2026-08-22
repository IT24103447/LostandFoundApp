using System.Net;
using System.Net.Mail;
using AuthService.Configuration;
using Microsoft.Extensions.Options;

namespace AuthService.Services;

/// <summary>
/// SMTP relay email service: uses Mailtrap sandbox in Development,
/// SendGrid SMTP relay (smtp.sendgrid.net:587) in Production.
/// No SDK change needed — same SmtpClient handles both providers.
/// </summary>
public class SmtpEmailService : IEmailService
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpSettings> settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default)
    {
        // Fire-and-log: SMTP failures must not break the calling flow (registration, resend).
        // We log a warning so monitoring (Application Insights) can alert on send failure rates.
        try
        {
            using var client = new SmtpClient(_settings.Host, _settings.Port)
            {
                Credentials = new NetworkCredential(_settings.User, _settings.Password),
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15_000,
            };

            using var message = new MailMessage
            {
                From = new MailAddress(_settings.FromAddress, _settings.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            message.To.Add(new MailAddress(to));

            if (!string.IsNullOrWhiteSpace(plainTextBody))
            {
                var plainView = AlternateView.CreateAlternateViewFromString(plainTextBody, null, "text/plain");
                message.AlternateViews.Add(plainView);
            }

            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Email sent to {Recipient} subject={Subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email send failed to {Recipient} subject={Subject}", to, subject);
        }
    }
}
