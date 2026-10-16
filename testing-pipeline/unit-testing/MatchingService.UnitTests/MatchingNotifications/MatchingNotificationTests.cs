using System.Text;
using MatchingService.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace MatchingService.UnitTests.MatchingNotifications;

public sealed class MatchingNotificationTests
{
    [Theory]
    [InlineData(NotificationTypes.CounterpartAction, "A claim was submitted for your report")]
    [InlineData(NotificationTypes.MatchConfirmed, "Your match was confirmed")]
    [InlineData(NotificationTypes.MatchRejected, "Your match was rejected")]
    public void SupportedOutcome_BuildsTheRightEmailWithOnlyItsAppLinkInPlainText(
        string type, string expectedSubject)
    {
        var job = Job(type);
        using var message = Sender().BuildMessage(job, "recipient@example.test");

        Assert.Equal(expectedSubject, message.Subject);
        Assert.Equal($"https://app.example.test/matched-items/{job.MatchId:D}", message.Body);
        Assert.Equal("recipient@example.test", Assert.Single(message.To).Address);
        Assert.False(message.IsBodyHtml);
    }

    [Theory]
    [InlineData(NotificationTypes.CounterpartAction)]
    [InlineData(NotificationTypes.MatchConfirmed)]
    [InlineData(NotificationTypes.MatchRejected)]
    public void SupportedOutcome_HtmlAlsoKeepsContactAndHiddenDetailsOutOfTheEmail(string type)
    {
        var job = Job(type);
        using var message = Sender().BuildMessage(job, "recipient@example.test");
        var htmlView = Assert.Single(message.AlternateViews);
        using var reader = new StreamReader(htmlView.ContentStream, Encoding.UTF8, leaveOpen: true);
        var html = reader.ReadToEnd();

        Assert.Contains($"https://app.example.test/matched-items/{job.MatchId:D}", html);
        Assert.DoesNotContain("recipient@example.test", html);
        Assert.DoesNotContain("phone", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNotificationType_IsNotSentAsAnOutcomeEmail()
    {
        var error = Assert.Throws<NotificationDeliveryException>(() =>
            Sender().BuildMessage(Job("AUTO_REJECTED_LOW_CONFIDENCE"), "recipient@example.test"));

        Assert.Equal("NOTIFICATION_TYPE_INVALID", error.Code);
    }

    [Fact]
    public void InvalidRecipient_IsRejectedBeforeBuildingAnEmail()
    {
        var error = Assert.Throws<NotificationDeliveryException>(() =>
            Sender().BuildMessage(Job(NotificationTypes.MatchConfirmed), "not-an-address"));

        Assert.Equal("RECIPIENT_EMAIL_INVALID", error.Code);
    }

    [Fact]
    public void ProductionFrontendLink_MustUseHttps()
    {
        Assert.Throws<InvalidOperationException>(() => Sender("http://app.example.test"));
    }

    private static NotificationJob Job(string type) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, 1, Guid.NewGuid());

    private static MatchEmailSender Sender(string frontendUrl = "https://app.example.test")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "smtp.example.test",
                ["Smtp:User"] = "test-user",
                ["Smtp:Password"] = "test-password",
                ["Smtp:FromAddress"] = "noreply@example.test",
                ["Frontend:BaseUrl"] = frontendUrl
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns("Production");
        return new MatchEmailSender(configuration, environment.Object);
    }
}
