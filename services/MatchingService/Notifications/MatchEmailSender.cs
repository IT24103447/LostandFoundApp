using System.Net;
using System.Net.Mail;
using System.Text;

namespace MatchingService.Notifications;

public sealed class MatchEmailSender : IMatchEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly Uri _frontendBaseUri;

    public MatchEmailSender(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _host = Required(configuration, "Smtp:Host");
        _username = Required(configuration, "Smtp:User");
        _password = Required(configuration, "Smtp:Password");
        _fromAddress = Required(configuration, "Smtp:FromAddress");
        _fromName = configuration["Smtp:FromName"] ?? "Lost & Found";
        _port = configuration.GetValue<int>("Smtp:Port", 587);

        if (_port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Smtp:Port is invalid.");
        }

        if (!MailAddress.TryCreate(_fromAddress, out _))
        {
            throw new InvalidOperationException(
                "Smtp:FromAddress must be a valid email address.");
        }

        var frontendUrl = Required(configuration, "Frontend:BaseUrl");

        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var frontendUri) ||
            (frontendUri.Scheme != Uri.UriSchemeHttp &&
             frontendUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(frontendUri.UserInfo) ||
            !string.IsNullOrEmpty(frontendUri.Query) ||
            !string.IsNullOrEmpty(frontendUri.Fragment) ||
            (!environment.IsDevelopment() &&
             frontendUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Frontend:BaseUrl must be a valid frontend URL, " +
                "using HTTPS outside Development.");
        }

        _frontendBaseUri = new Uri(
            frontendUri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task SendAsync(
        NotificationJob job,
        string recipientEmail,
        CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(recipientEmail, out var recipient))
        {
            throw new NotificationDeliveryException(
                "RECIPIENT_EMAIL_INVALID");
        }

        var subject = job.Type switch
        {
            NotificationTypes.CounterpartAction =>
                "A claim was submitted for your report",

            NotificationTypes.MatchConfirmed =>
                "Your match was confirmed",

            NotificationTypes.MatchRejected =>
                "Your match was rejected",

            _ => throw new NotificationDeliveryException(
                "NOTIFICATION_TYPE_INVALID")
        };

        var link = new Uri(
            _frontendBaseUri, $"matched-items/{job.MatchId:D}");

        using var message = new MailMessage
        {
            From = new MailAddress(_fromAddress, _fromName),
            Subject = subject,
            Body = link.AbsoluteUri,
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(recipient);

        using var client = new SmtpClient(_host, _port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_username, _password),
            EnableSsl = true
        };

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await client.SendMailAsync(message, timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException("SMTP_TIMEOUT");
        }
    }

    private static string Required(
        IConfiguration configuration,
        string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{key} is required when notifications are enabled.");
        }

        return value;
    }
}