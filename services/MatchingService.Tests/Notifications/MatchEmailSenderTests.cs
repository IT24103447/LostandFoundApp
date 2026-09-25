using MatchingService.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MatchingService.Tests.Notifications;

/// <summary>
/// Story 6 (email notifications). Pure, no-network coverage of MatchEmailSender.BuildMessage - the
/// subject-per-type switch, the matched-items/{id} link construction, recipient-address validation,
/// and the constructor's own config guards - none of which need real SMTP credentials, since dev split
/// message-building out from the actual client.SendMailAsync call specifically so this could be tested
/// without them (see Bugs_Sprint3.md history / commit b5fc189). Always runs in CI: dummy, well-formed
/// config values are all this needs, never real ones.
/// </summary>
public sealed class MatchEmailSenderTests
{
    private static MatchEmailSender CreateSender(
        string? host = "smtp.example.test",
        string? user = "test-user",
        string? password = "test-password",
        string? fromAddress = "noreply@example.com",
        string? frontendBaseUrl = "https://app.example.com",
        bool development = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = host,
                ["Smtp:User"] = user,
                ["Smtp:Password"] = password,
                ["Smtp:FromAddress"] = fromAddress,
                ["Frontend:BaseUrl"] = frontendBaseUrl
            })
            .Build();

        return new MatchEmailSender(configuration, new StubHostEnvironment(development));
    }

    private sealed class StubHostEnvironment(bool isDevelopment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = isDevelopment ? "Development" : "Production";
        public string ApplicationName { get; set; } = "MatchingService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static NotificationJob JobOfType(string type) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, 1, Guid.NewGuid());

    [Theory]
    [InlineData(NotificationTypes.CounterpartAction, "A claim was submitted for your report")]
    [InlineData(NotificationTypes.MatchConfirmed, "Your match was confirmed")]
    [InlineData(NotificationTypes.MatchRejected, "Your match was rejected")]
    public void BuildMessage_KnownType_PicksTheCorrectSubject(string type, string expectedSubject)
    {
        var sender = CreateSender();
        var job = JobOfType(type);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        Assert.Equal(expectedSubject, message.Subject);
    }

    [Fact]
    public void BuildMessage_UnknownType_ThrowsNotificationTypeInvalid()
    {
        var sender = CreateSender();
        var job = JobOfType("SOMETHING_ELSE");

        var exception = Assert.Throws<NotificationDeliveryException>(
            () => sender.BuildMessage(job, "recipient@example.com"));

        Assert.Equal("NOTIFICATION_TYPE_INVALID", exception.Code);
    }

    [Fact]
    public void BuildMessage_InvalidRecipientEmail_ThrowsRecipientEmailInvalid()
    {
        var sender = CreateSender();
        var job = JobOfType(NotificationTypes.MatchConfirmed);

        var exception = Assert.Throws<NotificationDeliveryException>(
            () => sender.BuildMessage(job, "not-an-email"));

        Assert.Equal("RECIPIENT_EMAIL_INVALID", exception.Code);
    }

    [Fact]
    public void BuildMessage_ValidRecipient_AddsExactlyThatRecipient()
    {
        var sender = CreateSender();
        var job = JobOfType(NotificationTypes.MatchConfirmed);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        var recipient = Assert.Single(message.To);
        Assert.Equal("recipient@example.com", recipient.Address);
    }

    [Fact]
    public void BuildMessage_Body_IsExactlyTheMatchedItemsLinkForThatMatchId()
    {
        var sender = CreateSender(frontendBaseUrl: "https://app.example.com");
        var job = JobOfType(NotificationTypes.MatchConfirmed);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        Assert.Equal($"https://app.example.com/matched-items/{job.MatchId:D}", message.Body);
    }

    [Fact]
    public void BuildMessage_FrontendBaseUrlWithoutTrailingSlash_StillProducesACorrectLink()
    {
        // The constructor normalizes BaseUrl to always end with a single trailing slash before
        // combining it with the relative "matched-items/{id}" path - this proves that normalization
        // doesn't produce a double slash or drop a path segment either way.
        var sender = CreateSender(frontendBaseUrl: "https://app.example.com/");
        var job = JobOfType(NotificationTypes.MatchRejected);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        Assert.Equal($"https://app.example.com/matched-items/{job.MatchId:D}", message.Body);
    }

    [Fact]
    public void BuildMessage_NeverIncludesAnyContactOrHiddenMatchingInformation()
    {
        // DoD: "containing only an app link... no contact information and no hidden matching
        // information in any of them." The body is the link alone - this pins that down so a future
        // change can't accidentally start interpolating anything else into it.
        var sender = CreateSender();
        var job = JobOfType(NotificationTypes.CounterpartAction);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        Assert.DoesNotContain("@", message.Body);
        Assert.False(message.IsBodyHtml);
    }

    [Fact]
    public void Constructor_MissingSmtpHost_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(host: null));

        Assert.Contains("Smtp:Host", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingSmtpUser_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(user: null));

        Assert.Contains("Smtp:User", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingSmtpPassword_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(password: null));

        Assert.Contains("Smtp:Password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingFromAddress_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(fromAddress: null));

        Assert.Contains("Smtp:FromAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MalformedFromAddress_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(fromAddress: "not-an-email"));

        Assert.Contains("FromAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingFrontendBaseUrl_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: null));

        Assert.Contains("Frontend:BaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://user:pass@app.example.com")]
    [InlineData("https://app.example.com?query=1")]
    [InlineData("https://app.example.com#fragment")]
    public void Constructor_MalformedFrontendBaseUrl_ThrowsInvalidOperationException(string frontendBaseUrl)
    {
        Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: frontendBaseUrl));
    }

    [Fact]
    public void Constructor_HttpFrontendBaseUrlOutsideDevelopment_ThrowsInvalidOperationException()
    {
        // Mirrors the rest of this project's own convention (Story 1's Blob/Gemini validation):
        // plain HTTP is only tolerated in Development, never in a deployed environment.
        Assert.Throws<InvalidOperationException>(
            () => CreateSender(frontendBaseUrl: "http://app.example.com", development: false));
    }

    [Fact]
    public void Constructor_HttpFrontendBaseUrlInDevelopment_IsAccepted()
    {
        var sender = CreateSender(frontendBaseUrl: "http://localhost:5173", development: true);
        var job = JobOfType(NotificationTypes.MatchConfirmed);

        using var message = sender.BuildMessage(job, "recipient@example.com");

        Assert.StartsWith("http://localhost:5173/matched-items/", message.Body, StringComparison.Ordinal);
    }
}
