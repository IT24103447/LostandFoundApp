using AdminVerifyService.Spam;
using Microsoft.Extensions.Configuration;

namespace AdminVerifyService.UnitTests.FlagPostingBursts;

public sealed class SpamAlertTests
{
    [Fact]
    public void NewRecordAlertContainsReviewLinkAndNoListingContent()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.example.test", ["Smtp:User"] = "test-user",
            ["Smtp:Password"] = "test-password", ["Smtp:FromAddress"] = "noreply@example.test",
            ["Frontend:BaseUrl"] = "https://app.example.test"
        }).Build();
        using var email = new SpamAlertEmailSender(config)
            .BuildMessage(new SpamAlertJob(Guid.NewGuid(), "admin@example.test", 1));
        Assert.Equal("admin@example.test", Assert.Single(email.To).Address);
        Assert.Equal("New Spam record needs review", email.Subject);
        Assert.Contains("https://app.example.test/admin/spam-review", email.Body);
        Assert.DoesNotContain("title", email.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", email.Body, StringComparison.OrdinalIgnoreCase);
    }
}
