using System.Net.Mail;
using System.Text;
using MatchingService.Appeals;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MatchingService.Tests.Appeals;

/// <summary>
/// Story LF-338 pure, no-network coverage of AppealEmailSender.BuildMessage — subjects, both body parts,
/// link construction, rejection-reason encoding, recipient validation and the config guards. No SMTP server,
/// no database and no Docker, so this class always runs in CI.
///
/// Deliberately modelled on Notifications/MatchEmailSenderTests.cs, with the differences this sender
/// actually exposes: it takes no IHostEnvironment (so a plain-HTTP frontend URL is accepted in every
/// environment, not just Development), it reports failures as InvalidOperationException rather than
/// NotificationDeliveryException, and the recipient lives on the job instead of being passed separately.
/// </summary>
public sealed class AppealEmailSenderTests
{
    private const string VerifiedLinkText = "View the match";
    private const string RejectedLinkText = "View your appeals";

    private static AppealEmailSender CreateSender(
        string? host = "smtp.example.test",
        string? user = "test-user",
        string? password = "test-password",
        string? fromAddress = "noreply@example.com",
        string? fromName = null,
        string? frontendBaseUrl = "https://app.example.com")
    {
        var values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = host,
            ["Smtp:User"] = user,
            ["Smtp:Password"] = password,
            ["Smtp:FromAddress"] = fromAddress,
            ["Frontend:BaseUrl"] = frontendBaseUrl
        };

        if (fromName is not null)
        {
            values["Smtp:FromName"] = fromName;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new AppealEmailSender(configuration);
    }

    private static AppealEmailJob Job(
        string type,
        string recipient = "recipient@example.com",
        string? rejectionReason = null,
        Guid? matchId = null) =>
        new(
            Guid.NewGuid(),
            type,
            recipient,
            Attempts: 1,
            rejectionReason,
            matchId ?? Guid.NewGuid());

    /// <summary>The plain-text part, which MailMessage exposes as Body.</summary>
    private static string Text(MailMessage message) => message.Body;

    /// <summary>The single text/html alternate view, read back as text so assertions can work on strings.</summary>
    private static string Html(MailMessage message)
    {
        var view = Assert.Single(message.AlternateViews);
        Assert.Equal("text/html", view.ContentType?.MediaType);

        using var reader = new StreamReader(view.ContentStream!);
        return reader.ReadToEnd();
    }

    private static string LinkFrom(string body)
    {
        // "http" rather than "https://" so the same helper also reads a plain-HTTP link.
        var start = body.IndexOf("http", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected a URL in the message body.");

        var end = start;
        while (end < body.Length && !char.IsWhiteSpace(body[end]) && body[end] != ')' && body[end] != '"')
        {
            end++;
        }

        return body[start..end];
    }

    // ---- subject ----

    [Fact]
    public void BuildMessage_VerifiedJob_UsesTheVerifiedSubject()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Verified));

        Assert.Equal("Your match appeal was verified", message.Subject);
    }

    [Fact]
    public void BuildMessage_RejectedJob_UsesTheRejectedSubject()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected));

        Assert.Equal("Your match appeal was rejected", message.Subject);
    }

    // ---- verified body ----

    [Fact]
    public void BuildMessage_VerifiedJob_TextBodySaysTheMatchIsWaitingForTheOtherUser()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Verified));

        var body = Text(message);

        Assert.Contains("An admin has verified your match appeal.", body, StringComparison.Ordinal);
        Assert.Contains(
            "The match is now waiting for the other user to confirm it.",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_VerifiedJob_TextBodyLinksToThatMatch()
    {
        var job = Job(AppealNotificationType.Verified);

        using var message = CreateSender().BuildMessage(job);

        Assert.Contains(
            $"View the match: https://app.example.com/matched-items/{job.MatchId:D}",
            Text(message),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_VerifiedJob_HtmlBodyLinksToThatMatch()
    {
        var job = Job(AppealNotificationType.Verified);

        using var message = CreateSender().BuildMessage(job);

        var html = Html(message);

        Assert.Contains($"href=\"https://app.example.com/matched-items/{job.MatchId:D}\"", html, StringComparison.Ordinal);
        Assert.Contains(VerifiedLinkText, html, StringComparison.Ordinal);
    }

    // ---- rejected body ----

    [Fact]
    public void BuildMessage_RejectedJob_TextBodySaysItWasRejected()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected));

        Assert.Contains("An admin has rejected your match appeal.", Text(message), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_RejectedJob_LinkGoesToTheAppealsTabNotToAMatch()
    {
        /* A rejected appeal never creates a match, so there is no match id to link to — the email has to
           send the appellant to their appeals list instead. */
        var job = Job(AppealNotificationType.Rejected, matchId: null);

        using var message = CreateSender().BuildMessage(job);

        Assert.Equal("https://app.example.com/matched-items?tab=appeals", LinkFrom(Text(message)));
        Assert.Contains(
            "href=\"https://app.example.com/matched-items?tab=appeals\"",
            Html(message),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_RejectedJobWithoutAReason_OmitsTheReasonLineEntirely()
    {
        /* The reason is optional. The failure mode being pinned down here is a dangling
           "Reason from the admin:" label with nothing after it. */
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected, rejectionReason: null));

        Assert.DoesNotContain("Reason from the admin", Text(message), StringComparison.Ordinal);
        Assert.DoesNotContain("Reason from the admin", Html(message), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_RejectedJobWithAWhitespaceReason_TreatsItAsNoReason()
    {
        /* DecideAsync trims the reason before storing it, but nothing guarantees an all-whitespace value
           can never reach the job — IsNullOrWhiteSpace is the guard, so prove it holds. */
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected, rejectionReason: "   "));

        Assert.DoesNotContain("Reason from the admin", Text(message), StringComparison.Ordinal);
        Assert.DoesNotContain("Reason from the admin", Html(message), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_RejectedJobWithAReason_IncludesItInTheTextBody()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected, rejectionReason: "Wrong bag"));

        Assert.Contains("Reason from the admin: Wrong bag", Text(message), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_RejectedJobWithAnHtmlReason_EncodesItInTheHtmlBody()
    {
        /* The admin types this reason, and it is rendered as HTML — so an unencoded value would be a
           stored-XSS hole in the appellant's inbox. The text part must stay raw (it is plain text), the
           HTML part must be encoded, and the markup must not survive into the HTML part. */
        const string reason = "<script>alert('xss')</script> & \"quotes\"";

        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Rejected, rejectionReason: reason));

        Assert.Contains(reason, Text(message), StringComparison.Ordinal);

        var html = Html(message);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
    }

    // ---- envelope ----

    [Fact]
    public void BuildMessage_AddsExactlyTheJobRecipient()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Verified, recipient: "appellant@example.com"));

        var recipient = Assert.Single(message.To);
        Assert.Equal("appellant@example.com", recipient.Address);
    }

    [Fact]
    public void BuildMessage_InvalidRecipient_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender().BuildMessage(
                Job(AppealNotificationType.Verified, recipient: "not-an-email")));

        Assert.Contains("not a valid email address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_UsesTheConfiguredFromAddressAndName()
    {
        using var message = CreateSender(
            fromAddress: "no-reply@example.test",
            fromName: "Lost & Found").BuildMessage(
            Job(AppealNotificationType.Verified));

        Assert.Equal("no-reply@example.test", message.From?.Address);
        Assert.Equal("Lost & Found", message.From?.DisplayName);
    }

    [Fact]
    public void BuildMessage_WithoutAConfiguredFromName_DefaultsToLostAndFound()
    {
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Verified));

        Assert.Equal("Lost & Found", message.From?.DisplayName);
    }

    [Fact]
    public void BuildMessage_IsPlainTextWithASingleUtf8HtmlAlternateView()
    {
        /* Mail clients pick the HTML view when there is one, so both parts have to exist and agree. The
           body is flagged non-HTML because the primary part is the plain-text one. */
        using var message = CreateSender().BuildMessage(
            Job(AppealNotificationType.Verified));

        Assert.False(message.IsBodyHtml);
        Assert.Equal(Encoding.UTF8, message.BodyEncoding);
        Assert.Equal(Encoding.UTF8, message.SubjectEncoding);

        var view = Assert.Single(message.AlternateViews);
        Assert.Equal("text/html", view.ContentType?.MediaType);
        Assert.Equal("utf-8", view.ContentType?.CharSet);
    }

    // ---- frontend base URL normalisation ----

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("https://app.example.com/")]
    public void BuildMessage_EitherTrailingSlashForm_ProducesTheSameSingleSlashLink(string baseUrl)
    {
        /* The constructor normalises BaseUrl to end with exactly one slash before combining it with the
           relative path, so neither input form may produce a doubled or missing slash. */
        var job = Job(AppealNotificationType.Verified);

        using var message = CreateSender(frontendBaseUrl: baseUrl).BuildMessage(job);

        Assert.Equal(
            $"https://app.example.com/matched-items/{job.MatchId:D}",
            LinkFrom(Text(message)));
    }

    [Fact]
    public void BuildMessage_PlainHttpFrontendUrl_IsAccepted()
    {
        /* Unlike MatchEmailSender there is no IHostEnvironment here, so http is never rejected — this is
           what lets the dev environment use http://localhost:5173. */
        var job = Job(AppealNotificationType.Verified);

        using var message = CreateSender(frontendBaseUrl: "http://localhost:5173").BuildMessage(job);

        Assert.Equal(
            $"http://localhost:5173/matched-items/{job.MatchId:D}",
            LinkFrom(Text(message)));
    }

    // ---- config guards ----

    [Theory]
    [InlineData("Smtp:Host")]
    [InlineData("Smtp:User")]
    [InlineData("Smtp:Password")]
    [InlineData("Smtp:FromAddress")]
    public void Constructor_MissingRequiredSmtpSetting_ThrowsNamingThatSetting(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(
                host: key == "Smtp:Host" ? null : "smtp.example.test",
                user: key == "Smtp:User" ? null : "test-user",
                password: key == "Smtp:Password" ? null : "test-password",
                fromAddress: key == "Smtp:FromAddress" ? null : "noreply@example.com"));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingFrontendBaseUrl_ThrowsNamingThatSetting()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: null));

        Assert.Contains("Frontend:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com")]
    public void Constructor_MalformedFrontendBaseUrl_Throws(string frontendBaseUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: frontendBaseUrl));

        Assert.Contains("Frontend:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    // ---- type handling ----

    [Fact]
    public void BuildMessage_NonVerifiedType_IsRenderedAsTheRejectedEmail()
    {
        /* Only AppealNotificationType.Verified is special-cased; everything else falls through to the
           rejected branch. The table constrains the column to those two values, so this documents the
           fall-through rather than expecting it to be reachable in practice. */
        using var message = CreateSender().BuildMessage(
            Job("APPEAL_SOMETHING_ELSE"));

        Assert.Equal("Your match appeal was rejected", message.Subject);
        Assert.DoesNotContain("waiting for the other user", Text(message), StringComparison.Ordinal);
    }
}
