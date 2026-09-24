using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Story 4 (lost reporter confirms or rejects a match) browser coverage: the shared review screen's
/// LostReporterDecisionPanel (the two-step "Confirm match"/"Reject match" -> "Yes, confirm/reject
/// match" flow), the resulting Confirmed/Rejected banners, and the Arrange The Return contact reveal
/// once both parties have confirmed. Reuses ClaimAndMatchFixture (real login, real report creation,
/// the same seeded accounts) since this is the same feature area as Stories 2/3, just the lost
/// reporter's own decision step.
///
/// A FINDER_CONFIRMED match - the state this panel needs - is reached the same way Story 2's own tests
/// reach LOST_REPORTER_CONFIRMED, just from the other item type's detail page: viewing a LOST item
/// shows "I Found This Item" instead of "This Is My Lost Item", and claiming through it makes the
/// caller the FINDER, leaving the match awaiting the LOST REPORTER's decision - exactly the state
/// LostReporterDecisionPanel is for.
///
/// Kept in its own file rather than folded into ClaimAndMatchFlowTests.cs or MatchedItemsPageFlowTests.cs,
/// since this is its own story with its own real backend/UI, not a read-side extension of either.
/// Scenario 4 (unauthorized access) and Scenario 5 (acting out of turn) are not retested here - both
/// are already proven at the xUnit level (LostReporterDecisionRepositoryTests) and are effectively
/// unreachable through the real UI anyway, since MatchReviewPage only ever renders this panel when the
/// viewer's own role and the match's own status already agree it's their turn.
/// </summary>
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

    // Scenario 2 + Scenario 6: confirming moves the match to Confirmed, shows the confirmed banner,
    // and reveals the finder's real stored contact details through the Arrange The Return panel - with
    // a working Copy button. The clipboard write can legitimately fail under Selenium/Chrome's
    // clipboard permission model, so the assertion accepts either the "Copied" success message or the
    // component's own manual-copy fallback, not just the first: what this test proves is that clicking
    // Copy produces feedback, not that this Chrome session specifically granted clipboard-write.
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

    // Scenario 3: rejecting closes the match, with the shared "Match closed: Rejected" banner shown
    // instead of any contact reveal - no further action is possible from here.
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

    // The two-step confirmation is a real, distinct UI step, not a formality: choosing "Confirm match"
    // and then "Go back" must return to the original choice with nothing submitted, so a misclick
    // never silently decides the match.
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

        // Still FINDER_CONFIRMED, not decided: reloading shows the same decision panel again, not a
        // banner or the waiting text.
        Driver.Navigate().Refresh();
        Wait.Until(d => d.PageSource.Contains("Is this your lost item?", StringComparison.Ordinal));
    }

    // ---- Helpers -----------------------------------------------------------

    // Mirror of MatchedItemsPageFlowTests.SubmitClaim, but from a LOST item's detail page ("I Found
    // This Item"), which makes the caller the FINDER and leaves the match at FINDER_CONFIRMED - the
    // state LostReporterDecisionPanel is for. Deliberately duplicated rather than shared, matching this
    // suite's existing convention: each file only ever needs its own one straight-through path to seed
    // the real match state it tests.
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
