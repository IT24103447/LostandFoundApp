using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 5: FinderDecisionPanel, the mirror image of Story 4's. Scenario 4/5 (unauthorized, out of
// turn) are already proven at the xUnit level and unreachable through the real UI.
[Trait("Story", "5")]
public sealed class FinderDecisionFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public FinderDecisionFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenarios 2 + 6: confirming shows the Confirmed banner and reveals the lost reporter's contact
    // via Arrange The Return, with a working Copy button.
    [Fact]
    public void FinderConfirmsMatch_MovesToConfirmedAndRevealsLostReporterContact()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story5 confirm compass";
        const string category = "Other";
        const string description = "A small brass pocket compass, lid slightly dented.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);
        SubmitClaimAsLostReporter(foundId, title);
        var matchId = _fixture.GetLatestMatchId(userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Does this match the item you found?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Confirm this match?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Yes, confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Match confirmed by both parties", StringComparison.Ordinal));
        Wait.Until(d => d.PageSource.Contains("Arrange the return", StringComparison.Ordinal));

        Wait.Until(d => !d.PageSource.Contains("Loading contact details", StringComparison.Ordinal));
        Assert.Contains(ClaimAndMatchFixture.UserAEmail, Driver.PageSource, StringComparison.Ordinal);

        Driver.FindElement(By.CssSelector("button[aria-label='Copy email']")).Click();
        var feedback = Wait.Until(d => d.FindElement(By.CssSelector("[role='status']")));
        Assert.False(string.IsNullOrWhiteSpace(feedback.Text));
    }

    // Scenario 3: rejecting shows the shared "Match closed: Rejected" banner, no contact reveal.
    [Fact]
    public void FinderRejectsMatch_ShowsClosedBanner()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story5 reject umbrella";
        const string category = "Other";
        const string description = "A folding black umbrella with a cracked handle.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);
        SubmitClaimAsLostReporter(foundId, title);
        var matchId = _fixture.GetLatestMatchId(userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Does this match the item you found?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Reject match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Reject this match?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Yes, reject match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Match closed: Rejected", StringComparison.Ordinal));
    }

    // Confirm then Go back returns to the initial choice without submitting anything.
    [Fact]
    public void FinderSelectsConfirm_ThenGoesBack_ReturnsToInitialChoiceWithoutSubmitting()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story5 goback bracelet";
        const string category = "Accessories";
        const string description = "A thin silver-tone chain bracelet, clasp missing.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);
        SubmitClaimAsLostReporter(foundId, title);
        var matchId = _fixture.GetLatestMatchId(userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Does this match the item you found?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Confirm this match?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Go back')]")).Click();
        Wait.Until(d => d.FindElements(By.XPath("//button[contains(.,'Confirm match')]")).Count > 0);
        Assert.True(Driver.FindElement(By.XPath("//button[contains(.,'Reject match')]")).Displayed);

        Driver.Navigate().Refresh();
        Wait.Until(d => d.PageSource.Contains("Does this match the item you found?", StringComparison.Ordinal));
    }

    // ---- Helpers -----------------------------------------------------------

    private void SubmitClaimAsLostReporter(Guid targetFoundItemId, string candidateTitle)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/items/{targetFoundItemId}");
        Wait.Until(d => d.FindElement(By.XPath("//button[contains(.,'This Is My Lost Item')]"))).Click();
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
}
