using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace MatchingService.Appeals;

public interface IAppealEmailSender
{
    Task SendAsync(AppealEmailJob job, CancellationToken cancellationToken);
}

public sealed class AppealEmailSender : IAppealEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly Uri _frontendBaseUri;

    public AppealEmailSender(IConfiguration configuration)
    {
        _host = Required(configuration, "Smtp:Host");
        _username = Required(configuration, "Smtp:User");
        _password = Required(configuration, "Smtp:Password");
        _fromAddress = Required(configuration, "Smtp:FromAddress");
        _fromName = configuration["Smtp:FromName"] ?? "Lost & Found";
        _port = configuration.GetValue("Smtp:Port", 587);

        var frontendUrl = Required(configuration, "Frontend:BaseUrl");

        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var frontendUri) ||
            (frontendUri.Scheme != Uri.UriSchemeHttp &&
             frontendUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Frontend:BaseUrl must be a valid frontend URL.");
        }

        _frontendBaseUri = new Uri(frontendUri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public async Task SendAsync(
        AppealEmailJob job,
        CancellationToken cancellationToken)
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

    public MailMessage BuildMessage(AppealEmailJob job)
    {
        if (!MailAddress.TryCreate(job.RecipientEmail, out var recipient))
        {
            throw new InvalidOperationException("The appeal email recipient is not a valid email address.");
        }

        var verified = job.Type == AppealNotificationType.Verified;

        var link = verified
            ? new Uri(_frontendBaseUri, $"matched-items/{job.MatchId:D}")
            : new Uri(_frontendBaseUri, "matched-items?tab=appeals");

        var subject = verified
            ? "Your match appeal was verified"
            : "Your match appeal was rejected";

        var message = new MailMessage
        {
            From = new MailAddress(_fromAddress, _fromName),
            Subject = subject,
            Body = BuildTextBody(job, verified, link),
            IsBodyHtml = false,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(recipient);
        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(
                BuildHtmlBody(job, verified, link),
                Encoding.UTF8,
                MediaTypeNames.Text.Html));

        return message;
    }

    private static string BuildTextBody(AppealEmailJob job, bool verified, Uri link)
    {
        if (verified)
        {
            return "An admin has verified your match appeal. The match is now waiting for the other user to confirm it."
                + $"\n\nView the match: {link.AbsoluteUri}\n\nLost & Found";
        }

        var reason = string.IsNullOrWhiteSpace(job.RejectionReason)
            ? string.Empty
            : $"\n\nReason from the admin: {job.RejectionReason}";

        return "An admin has rejected your match appeal." + reason
            + $"\n\nView your appeals: {link.AbsoluteUri}\n\nLost & Found";
    }

    private static string BuildHtmlBody(AppealEmailJob job, bool verified, Uri link)
    {
        var href = WebUtility.HtmlEncode(link.AbsoluteUri);

        if (verified)
        {
            return "<p>An admin has verified your match appeal. The match is now waiting for the other user to confirm it.</p>"
                + $"<p><a href=\"{href}\">View the match</a></p><p>Lost &amp; Found</p>";
        }

        var reason = string.IsNullOrWhiteSpace(job.RejectionReason)
            ? string.Empty
            : $"<p>Reason from the admin: {WebUtility.HtmlEncode(job.RejectionReason)}</p>";

        return "<p>An admin has rejected your match appeal.</p>" + reason
            + $"<p><a href=\"{href}\">View your appeals</a> to see the details.</p><p>Lost &amp; Found</p>";
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
