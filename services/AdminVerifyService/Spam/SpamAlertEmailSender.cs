using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace AdminVerifyService.Spam;

public interface ISpamAlertEmailSender
{
    Task SendAsync(SpamAlertJob job, CancellationToken cancellationToken);
}

public sealed class SpamAlertEmailSender : ISpamAlertEmailSender
{
    private const string Subject = "New Spam record needs review";
    private const string Sentence = "A new Spam record has been created and needs review.";

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly Uri _reviewLink;

    public SpamAlertEmailSender(IConfiguration configuration)
    {
        _host = Required(configuration, "Smtp:Host");
        _username = Required(configuration, "Smtp:User");
        _password = Required(configuration, "Smtp:Password");
        _fromAddress = Required(configuration, "Smtp:FromAddress");
        _fromName = configuration["Smtp:FromName"] ?? "Lost & Found";
        _port = configuration.GetValue("Smtp:Port", 587);

        if (!Uri.TryCreate(Required(configuration, "Frontend:BaseUrl"), UriKind.Absolute, out var frontendUri))
        {
            throw new InvalidOperationException("Frontend:BaseUrl must be a valid frontend URL.");
        }

        _reviewLink = new Uri(new Uri(frontendUri.AbsoluteUri.TrimEnd('/') + "/"), "admin/spam-review");
    }

    public async Task SendAsync(SpamAlertJob job, CancellationToken cancellationToken)
    {
        using var message = BuildMessage(job);

        using var client = new SmtpClient(_host, _port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_username, _password),
            EnableSsl = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await client.SendMailAsync(message, timeout.Token);
    }

    public MailMessage BuildMessage(SpamAlertJob job)
    {
        if (!MailAddress.TryCreate(job.RecipientEmail, out var recipient))
        {
            throw new InvalidOperationException("The spam alert recipient is not a valid email address.");
        }

        var link = _reviewLink.AbsoluteUri;
        var href = WebUtility.HtmlEncode(link);

        var message = new MailMessage
        {
            From = new MailAddress(_fromAddress, _fromName),
            Subject = Subject,
            Body = $"{Sentence}\n\nOpen Spam Review: {link}\n\nLost & Found",
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(recipient);
        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(
                $"<p>{Sentence}</p><p><a href=\"{href}\">Open Spam Review</a></p><p>Lost &amp; Found</p>",
                Encoding.UTF8,
                MediaTypeNames.Text.Html));

        return message;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required when notifications are enabled.");
        }

        return value;
    }
}
