using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 4: LostReporterDecisionPanel - confirm/reject, the resulting banners, and the Arrange The
// Return contact reveal. Scenario 4/5 (unauthorized, out of turn) are already proven at the xUnit
// level and unreachable through the real UI.
[Trait("Story", "4")]
public sealed class LostReporterDecisionFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public LostReporterDecisionFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenarios 2 + 6: confirming shows the Confirmed banner and reveals the finder's contact via
    // Arrange The Return, with a working Copy button.
    [Fact]
    public void LostReporterConfirmsMatch_MovesToConfirmedAndRevealsFinderContact()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story4 confirm briefcase";
        const string category = "Bags";
        const string description = "A brown leather briefcase with a brass buckle.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);
        SubmitClaimAsFinder(lostId, title);
        var matchId = _fixture.GetLatestMatchId(userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Is this your lost item?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Confirm this is your item?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Yes, confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Match confirmed by both parties", StringComparison.Ordinal));
        Wait.Until(d => d.PageSource.Contains("Arrange the return", StringComparison.Ordinal));

        Wait.Until(d => !d.PageSource.Contains("Loading contact details", StringComparison.Ordinal));
        Assert.Contains(ClaimAndMatchFixture.UserBEmail, Driver.PageSource, StringComparison.Ordinal);

        Driver.FindElement(By.CssSelector("button[aria-label='Copy email']")).Click();
        var feedback = Wait.Until(d => d.FindElement(By.CssSelector("[role='status']")));
        Assert.False(string.IsNullOrWhiteSpace(feedback.Text));
    }

    // Scenario 3: rejecting shows the shared "Match closed: Rejected" banner, no contact reveal.
    [Fact]
    public void LostReporterRejectsMatch_ShowsClosedBanner()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story4 reject scarf";
        const string category = "Clothing";
        const string description = "A red wool scarf with a small tear at one end.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);
        SubmitClaimAsFinder(lostId, title);
        var matchId = _fixture.GetLatestMatchId(userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Is this your lost item?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Reject match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Reject this match?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Yes, reject match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Match closed: Rejected", StringComparison.Ordinal));
    }

    // Confirm then Go back returns to the initial choice without submitting anything.
    [Fact]
    public void LostReporterSelectsConfirm_ThenGoesBack_ReturnsToInitialChoiceWithoutSubmitting()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story4 goback lantern";
        const string category = "Other";
        const string description = "A dented tin camping lantern, no glass.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);
        SubmitClaimAsFinder(lostId, title);
        var matchId = _fixture.GetLatestMatchId(userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Is this your lost item?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Confirm match')]")).Click();
        Wait.Until(d => d.PageSource.Contains("Confirm this is your item?", StringComparison.Ordinal));

        Driver.FindElement(By.XPath("//button[contains(.,'Go back')]")).Click();
        Wait.Until(d => d.FindElements(By.XPath("//button[contains(.,'Confirm match')]")).Count > 0);
        Assert.True(Driver.FindElement(By.XPath("//button[contains(.,'Reject match')]")).Displayed);

        Driver.Navigate().Refresh();
        Wait.Until(d => d.PageSource.Contains("Is this your lost item?", StringComparison.Ordinal));
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
}
