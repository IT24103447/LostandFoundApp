using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Story 5 (finder confirms or rejects a match) browser coverage: the shared review screen's
/// FinderDecisionPanel - the mirror image of Story 4's LostReporterDecisionPanel, same two-step
/// "Confirm match"/"Reject match" -> "Yes, confirm/reject match" flow, same resulting banners, same
/// Arrange The Return contact reveal once both parties have confirmed. Reuses ClaimAndMatchFixture
/// (real login, real report creation, the same seeded accounts).
///
/// A LOST_REPORTER_CONFIRMED match - the state this panel needs - is reached exactly the way Story 2's
/// own tests already reach it: claiming through a FOUND item's "This Is My Lost Item" button makes the
/// caller the LOST REPORTER, leaving the match awaiting the FINDER's decision.
///
/// Kept in its own file rather than folded into ClaimAndMatchFlowTests.cs, MatchedItemsPageFlowTests.cs,
/// or LostReporterDecisionFlowTests.cs, since this is its own story with its own real backend/UI, not a
/// duplicate of Story 4's. Scenario 4 (unauthorized access) and Scenario 5 (acting out of turn) are not
/// retested here for the same reason as Story 4: already proven at the xUnit level
/// (FinderDecisionRepositoryTests) and effectively unreachable through the real UI, since MatchReviewPage
/// only ever renders this panel when the viewer's own role and the match's own status already agree
/// it's their turn.
/// </summary>
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

    // Scenario 2 + Scenario 6: confirming moves the match to Confirmed, shows the confirmed banner,
    // and reveals the lost reporter's real stored contact details through the Arrange The Return panel
    // - with a working Copy button. As in the Story 4 equivalent, the assertion accepts either the
    // "Copied" success message or the component's own manual-copy fallback, since a real Chrome
    // session's clipboard-write permission can legitimately go either way under Selenium.
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

    // Scenario 3: rejecting closes the match, with the shared "Match closed: Rejected" banner shown
    // instead of any contact reveal - no further action is possible from here.
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

    // The two-step confirmation is a real, distinct UI step, not a formality: choosing "Confirm match"
    // and then "Go back" must return to the original choice with nothing submitted, so a misclick
    // never silently decides the match.
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

        // Still LOST_REPORTER_CONFIRMED, not decided: reloading shows the same decision panel again,
        // not a banner or the waiting text.
        Driver.Navigate().Refresh();
        Wait.Until(d => d.PageSource.Contains("Does this match the item you found?", StringComparison.Ordinal));
    }

    // ---- Helpers -----------------------------------------------------------

    // Same straight-through claim path MatchedItemsPageFlowTests.SubmitClaim already uses (a FOUND
    // item's "This Is My Lost Item" button, making the caller the LOST REPORTER), duplicated here
    // rather than shared, matching this suite's existing convention.
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
