using System.Net.Mail;
using System.Text;
using AdminVerifyService.Spam;
using Microsoft.Extensions.Configuration;

namespace AdminVerifyService.Tests.Spam;

/// <summary>
/// Story LF-82 pure, no-network coverage of SpamAlertEmailSender.BuildMessage - the subject,
/// both body parts, the Spam Review link, recipient validation and the configuration guards.
/// No SMTP server, no database and no Docker, so this class always runs in CI.
///
/// Deliberately modelled on MatchingService's AppealEmailSenderTests (LF-338), including the
/// http/https Frontend:BaseUrl scheme guard regression pin: this sender took only a bare
/// Uri.TryCreate, repeating the LF-338 defect, so Constructor_MalformedFrontendBaseUrl_Throws
/// carries ftp:// and file:// cases that are red against the old guard on every platform and
/// green against the fixed one. Plain-http frontend URLs are accepted in every environment
/// because this sender - like AppealEmailSender - takes IConfiguration and no IHostEnvironment.
/// </summary>
public sealed class SpamAlertEmailSenderTests
{
    private const string ReviewLink = "https://app.example.com/admin/spam-review";

    private static SpamAlertEmailSender CreateSender(
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

        return new SpamAlertEmailSender(configuration);
    }

    private static SpamAlertJob Job(string recipient = "recipient@example.com") =>
        new(Guid.NewGuid(), recipient, Attempts: 1);

    private static string Html(MailMessage message)
    {
        var view = Assert.Single(message.AlternateViews);
        Assert.Equal("text/html", view.ContentType?.MediaType);

        using var reader = new StreamReader(view.ContentStream!);
        return reader.ReadToEnd();
    }

    // ---- subject ----

    [Fact]
    public void BuildMessage_UsesTheSpamReviewSubject()
    {
        using var message = CreateSender().BuildMessage(Job());

        Assert.Equal("New Spam record needs review", message.Subject);
    }

    // ---- body parts and the Spam Review link ----

    [Fact]
    public void BuildMessage_TextBodyContainsTheSentenceAndTheSpamReviewLink()
    {
        using var message = CreateSender().BuildMessage(Job());

        Assert.Equal(
            "A new Spam record has been created and needs review.\n\n" +
            $"Open Spam Review: {ReviewLink}\n\nLost & Found",
            message.Body);
    }

    [Fact]
    public void BuildMessage_HtmlBodyLinksToSpamReview()
    {
        using var message = CreateSender().BuildMessage(Job());

        Assert.Equal(
            "<p>A new Spam record has been created and needs review.</p>" +
            $"<p><a href=\"{ReviewLink}\">Open Spam Review</a></p>" +
            "<p>Lost &amp; Found</p>",
            Html(message));
    }

    // ---- envelope ----

    [Fact]
    public void BuildMessage_AddsExactlyTheJobRecipient()
    {
        using var message = CreateSender().BuildMessage(Job("alice@example.com"));

        var recipient = Assert.Single(message.To);
        Assert.Equal("alice@example.com", recipient.Address);
    }

    [Fact]
    public void BuildMessage_InvalidRecipient_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender().BuildMessage(Job("not-an-email")));

        Assert.Equal("The spam alert recipient is not a valid email address.", exception.Message);
    }

    [Fact]
    public void BuildMessage_UsesTheConfiguredFromAddressAndName()
    {
        using var message = CreateSender(fromName: "Spam Bot").BuildMessage(Job());

        Assert.NotNull(message.From);
        var from = message.From!;
        Assert.Equal("noreply@example.com", from.Address);
        Assert.Equal("Spam Bot", from.DisplayName);
    }

    [Fact]
    public void BuildMessage_WithoutAConfiguredFromName_DefaultsToLostAndFound()
    {
        using var message = CreateSender().BuildMessage(Job());

        Assert.NotNull(message.From);
        var from = message.From!;
        Assert.Equal("Lost & Found", from.DisplayName);
    }

    [Fact]
    public void BuildMessage_IsPlainTextWithASingleUtf8HtmlAlternateView()
    {
        using var message = CreateSender().BuildMessage(Job());

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
    [InlineData("https://app.example.com//")]
    public void BuildMessage_AllTrailingSlashForms_ProduceTheSameSingleSlashLink(string frontendBaseUrl)
    {
        using var message = CreateSender(frontendBaseUrl: frontendBaseUrl).BuildMessage(Job());

        Assert.Contains($"Open Spam Review: {ReviewLink}", message.Body);
    }

    [Fact]
    public void BuildMessage_APlainHttpFrontendUrl_IsAccepted()
    {
        using var message = CreateSender(frontendBaseUrl: "http://localhost:5173").BuildMessage(Job());

        Assert.Contains("Open Spam Review: http://localhost:5173/admin/spam-review", message.Body);
    }

    // ---- config guards ----

    [Theory]
    [InlineData("Smtp:Host")]
    [InlineData("Smtp:User")]
    [InlineData("Smtp:Password")]
    [InlineData("Smtp:FromAddress")]
    public void Constructor_MissingRequiredSmtpSetting_ThrowsNamingThatSetting(string key)
    {
        Func<SpamAlertEmailSender> create = key switch
        {
            "Smtp:Host" => () => CreateSender(host: null),
            "Smtp:User" => () => CreateSender(user: null),
            "Smtp:Password" => () => CreateSender(password: null),
            _ => () => CreateSender(fromAddress: null)
        };

        var exception = Assert.Throws<InvalidOperationException>(create);
        Assert.Contains(key, exception.Message);
    }

    [Fact]
    public void Constructor_MissingFrontendBaseUrl_ThrowsNamingThatSetting()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: null));

        Assert.Contains("Frontend:BaseUrl", exception.Message);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]     // accepted as file:// on Unix, rejected on Windows - either way it must fail
    [InlineData("ftp://example.com")]  // absolute but the wrong scheme - red against the old guard on every platform
    [InlineData("file:///etc/passwd")] // absolute file URI - rejected by the scheme check
    public void Constructor_MalformedFrontendBaseUrl_Throws(string badFrontendBaseUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: badFrontendBaseUrl));

        Assert.Equal("Frontend:BaseUrl must be a valid frontend URL.", exception.Message);
    }
}