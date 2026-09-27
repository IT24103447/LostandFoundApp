using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 8: editing a report until it no longer matches deactivates the pending match, reusing Story
// 7's Deactivated-display component with a new reason label/message. Scenarios 2-4 and the job-queue
// mechanics are already covered at the xUnit level (MatchReevaluationWorkerTests/RepositoryTests);
// this proves only the one new frontend surface - deactivationLabel/deactivationMessage's new
// ITEM_UPDATED_NO_LONGER_MATCHES case - actually renders, not the fallback generic text.
[Trait("Story", "8")]
public sealed class MatchReevaluationFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public MatchReevaluationFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenario 1 + Scenario 2, end to end through the real UI: editing the lost report until it no
    // longer matches deactivates the still-pending match, showing the new label/message rather than
    // the generic fallback text.
    [Fact]
    public void EditingTheLostReport_DropsScoreBelowThreshold_DeactivatesTheMatch_ShowsReportChangedOnReviewScreen()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story8 reevaluation umbrella";
        const string category = "Other";
        const string description = "A striped beach umbrella missing one pole segment.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);
        SubmitClaimAsFinder(lostId, title);
        var matchId = _fixture.GetLatestMatchId(userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        EditOwnLostReport(
            title,
            newTitle: "Selenium story8 completely different laptop bag",
            newCategory: "Electronics",
            newDescription: "A black laptop sleeve with a broken zipper, nothing like the original report.");

        WaitForMatchDeactivation(matchId, "ITEM_UPDATED_NO_LONGER_MATCHES");
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Report changed", StringComparison.Ordinal));

        Assert.Contains(
            "no longer meet the 60% similarity threshold", Driver.PageSource, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    private void SubmitClaimAsFinder(Guid targetLostItemId, string candidateTitle)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/items/{targetLostItemId}");
        Wait.Until(d => d.FindElement(By.XPath("//button[contains(.,'I Found This Item')]"))).Click();
        Wait.Until(d => d.FindElement(By.CssSelector("dialog[aria-labelledby='claim-dialog-title']")));

        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));
        Driver.FindElement(By.XPath(
            $"//button[.//span[normalize-space()='{candidateTitle}']]")).Click();

        Wait.Until(d => d.PageSource.Contains("Similarity score", StringComparison.Ordinal));
        var confirmButton = Driver.FindElement(By.XPath("//button[contains(.,'Confirm and claim')]"));
        Assert.True(confirmButton.Enabled, "Expected identical title/category/description to score above threshold.");
        confirmButton.Click();

        Wait.Until(d => d.PageSource.Contains("Claim submitted", StringComparison.Ordinal));
    }

    private void EditOwnLostReport(
        string currentTitle, string newTitle, string newCategory, string newDescription)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/my-reports");
        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        var card = Wait.Until(d => d.FindElement(By.XPath(
            $"//p[normalize-space()='{currentTitle}']/ancestor::div[contains(@class,'rounded-2xl')][1]")));
        card.FindElement(By.XPath(".//button[normalize-space()='Edit']")).Click();

        Wait.Until(d => d.PageSource.Contains("Edit Lost Item Report", StringComparison.Ordinal));

        var titleField = Driver.FindElement(By.Id("title"));
        titleField.Clear();
        titleField.SendKeys(newTitle);

        // CategorySelect is a custom button+listbox, not a native <select>.
        Driver.FindElement(By.Id("category")).Click();
        Driver.FindElement(By.XPath($"//button[normalize-space()='{newCategory}']")).Click();

        var descriptionField = Driver.FindElement(By.Id("description"));
        descriptionField.Clear();
        descriptionField.SendKeys(newDescription);

        // Never pre-filled by design (EditLostItemPage), so it must be re-entered on every save.
        Driver.FindElement(By.Id("hiddenInformation")).SendKeys("Selenium story8 re-entered hidden info");

        Driver.FindElement(By.XPath("//button[normalize-space()='Save Changes']")).Click();
        Wait.Until(d => d.PageSource.Contains("Changes saved.", StringComparison.Ordinal));
    }

    // Real Kafka consumer and background worker, polled directly rather than assumed synchronous.
    private void WaitForMatchDeactivation(Guid matchId, string expectedReason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_fixture.GetMatchDeactivationReason(matchId) == expectedReason) return;
            Thread.Sleep(1000);
        }

        throw new Xunit.Sdk.XunitException(
            $"Match {matchId} was never deactivated with reason '{expectedReason}' within 90s.");
    }
}
