using OpenQA.Selenium;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Story 3 (view potential matches) browser coverage: the dedicated Matches page (four sections,
/// pagination, empty/loading/error states), the shared review screen's read-only states, ownership
/// isolation, and the profile page's live-count tile. Reuses ClaimAndMatchFixture (real login, real
/// report creation, the same seeded accounts) since this is the same feature area as Story 2's claim
/// flow, just the read side.
///
/// Confirm/Reject actions (Scenario 5/6's actionable case) are not covered here: neither a backend
/// endpoint nor real UI controls for them exist yet (Bugs_Sprint3.md, Bug #3). CONFIRMED, REJECTED,
/// AUTO_REJECTED_LOW_CONFIDENCE, and deactivated matches - none reachable through any real endpoint
/// today - are seeded directly (ClaimAndMatchFixture.InsertMatchDirectly), the same justified pattern
/// MatchReadRepositoryTests already uses at the xUnit level for the same reason.
/// </summary>
[Trait("Story", "3")]
public sealed class MatchedItemsPageFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public MatchedItemsPageFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenarios 2 + 4: a real half-confirmed match's row shows the other party's item, the confidence
    // percentage, and a role label without expanding anything; expanding "View details" additionally
    // reveals category/date/location; the row's review link opens the shared review screen.
    [Fact]
    public void MatchesPage_RowShowsOtherPartyAndConfidence_AndOpensSharedReviewScreen()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium matches-page teal lantern";
        const string category = "Other";
        const string description = "A teal camping lantern with a cracked handle.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        SubmitClaim(foundId, title);
        var matchId = _fixture.GetLatestMatchId(userAId);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");
        Wait.Until(d => d.PageSource.Contains(title, StringComparison.Ordinal));

        // Visible on the row itself, without expanding: role label and confidence percentage.
        Assert.Contains("You submitted this claim", Driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Similarity", Driver.PageSource, StringComparison.Ordinal);

        Driver.FindElement(By.XPath("//button[contains(.,'View details')]")).Click();
        Wait.Until(d => d.PageSource.Contains(category, StringComparison.Ordinal));
        Assert.Contains("Test location", Driver.PageSource, StringComparison.Ordinal);

        Driver.FindElement(By.XPath($"//a[@href='/matched-items/{matchId}']")).Click();
        Wait.Until(d => d.Url.Contains($"/matched-items/{matchId}", StringComparison.Ordinal));
        Wait.Until(d => d.PageSource.Contains(title, StringComparison.Ordinal));
    }

    // Scenario 6: the claimant who already confirmed their own side sees read-only waiting text on the
    // review screen, never Confirm/Reject controls - a real, honest assertion of today's behavior, not
    // a claim that Scenario 5's actionable case (the other party's side) is implemented.
    [Fact]
    public void ReviewScreen_ClaimantWaitingOnOtherParty_ShowsReadOnlyText_NoConfirmOrRejectButtons()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium read-only-state amber ring";
        const string category = "Accessories";
        const string description = "A thin amber-stone ring, slightly bent.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        SubmitClaim(foundId, title);
        var matchId = _fixture.GetLatestMatchId(userAId);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains(
            "You confirmed your claim. The other person needs to review it and decide.",
            StringComparison.Ordinal));

        Assert.Empty(Driver.FindElements(By.XPath("//button[contains(.,'Confirm')]")));
        Assert.Empty(Driver.FindElements(By.XPath("//button[contains(.,'Reject')]")));
    }

    // Scenario 3: matches are grouped into all four sections, and Rejected starts collapsed while the
    // other three start expanded. CONFIRMED/REJECTED matches are seeded directly, since nothing in the
    // real product can move a match into either status yet.
    [Fact]
    public void MatchesPage_FourSections_RejectedCollapsedByDefault_OthersExpanded()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        _fixture.InsertMatchDirectly(
            userAId, Guid.NewGuid(), "CONFIRMED",
            "Selenium seeded confirmed lost", "Selenium seeded confirmed found");
        _fixture.InsertMatchDirectly(
            userAId, Guid.NewGuid(), "REJECTED",
            "Selenium seeded rejected lost", "Selenium seeded rejected found");

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");

        Wait.Until(d => d.FindElements(By.XPath(
            "//button[@aria-expanded and .//span[normalize-space()='Confirmed']]")).Count > 0);

        AssertSectionExpanded("Waiting On You", true);
        AssertSectionExpanded("Waiting On The Other Party", true);
        AssertSectionExpanded("Confirmed", true);
        AssertSectionExpanded("Rejected", false);

        Wait.Until(d => d.PageSource.Contains("Selenium seeded confirmed lost", StringComparison.Ordinal));

        // Expanding Rejected reveals the seeded rejected match.
        var rejectedHeader = SectionHeaderButton("Rejected");
        rejectedHeader.Click();
        Wait.Until(d => rejectedHeader.GetAttribute("aria-expanded") == "true");
        Wait.Until(d => d.PageSource.Contains("Selenium seeded rejected lost", StringComparison.Ordinal));
    }

    // Scenario 7: the review screen shows the closed banner for a Rejected match.
    [Fact]
    public void ReviewScreen_RejectedMatch_ShowsClosedBanner()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var matchId = _fixture.InsertMatchDirectly(
            userAId, Guid.NewGuid(), "REJECTED",
            "Selenium banner-check rejected lost", "Selenium banner-check rejected found");

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");

        Wait.Until(d => d.PageSource.Contains("Match closed", StringComparison.Ordinal));
        Assert.Contains("Match closed: Rejected", Driver.PageSource, StringComparison.Ordinal);
    }

    // Scenario 8: Auto Rejected Low Confidence and deactivated matches never appear anywhere in the
    // list, even after expanding every section (including the normally-collapsed Rejected one).
    [Fact]
    public void MatchesPage_AutoRejectedAndDeactivatedMatches_NeverAppearInAnySection()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        const string autoRejectedTitle = "Selenium hidden auto-rejected lost";
        const string deactivatedTitle = "Selenium hidden deactivated lost";

        _fixture.InsertMatchDirectly(
            userAId, Guid.NewGuid(), "AUTO_REJECTED_LOW_CONFIDENCE",
            autoRejectedTitle, "Selenium hidden auto-rejected found");
        _fixture.InsertMatchDirectly(
            userAId, Guid.NewGuid(), "CONFIRMED",
            deactivatedTitle, "Selenium hidden deactivated found", isActive: false);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");

        Wait.Until(d => d.FindElements(By.XPath(
            "//button[@aria-expanded and .//span[normalize-space()='Confirmed']]")).Count > 0);
        SectionHeaderButton("Rejected").Click();
        Wait.Until(d => SectionHeaderButton("Rejected").GetAttribute("aria-expanded") == "true");

        Assert.DoesNotContain(autoRejectedTitle, Driver.PageSource, StringComparison.Ordinal);
        Assert.DoesNotContain(deactivatedTitle, Driver.PageSource, StringComparison.Ordinal);
    }

    // Scenario 9: a user who is neither party to a match gets a forbidden error, not the match's data.
    [Fact]
    public void ReviewScreen_MatchBelongsToAnotherUser_ShowsForbiddenErrorState()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);
        var matchId = _fixture.InsertMatchDirectly(
            userAId, userBId, "CONFIRMED",
            "Selenium isolation-check lost", "Selenium isolation-check found");

        var (_, strangerEmail, strangerPassword) = _fixture.GetOrRegisterFreshVerifiedUser();
        _fixture.LoginAs(strangerEmail, strangerPassword);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");

        var alertText = WaitForAlertText();
        Assert.False(string.IsNullOrWhiteSpace(alertText));
        Assert.DoesNotContain("Selenium isolation-check", Driver.PageSource, StringComparison.Ordinal);
    }

    // Scenario 10: a genuinely new account, freshly registered and never having claimed or been
    // claimed against, sees the empty-state message. The two long-lived seeded accounts (UserA/UserB)
    // can't stand in for this - they accumulate real matches across every earlier test in this suite.
    [Fact]
    public void MatchesPage_FreshAccountWithNoMatches_ShowsEmptyStateMessage()
    {
        var (_, email, password) = _fixture.GetOrRegisterFreshVerifiedUser();
        _fixture.LoginAs(email, password);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");

        Wait.Until(d => d.PageSource.Contains("No potential matches yet", StringComparison.Ordinal));
    }

    // Scenario 11: while the page's request is in flight, a loading indicator is shown. A heavily
    // throttled (not fully offline) network keeps the request pending long enough to observe this
    // deterministically, then lets it complete normally so the test still finishes.
    [Fact]
    public void MatchesPage_RequestInFlight_ShowsLoadingIndicator()
    {
        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");
        WaitForInitialPageLoad();

        SetNetworkConditions(offline: false, latencyMs: 4000, throughputBytesPerSecond: 2000);
        try
        {
            Driver.FindElement(By.XPath("//button[contains(.,'Refresh')]")).Click();
            Wait.Until(d => d.PageSource.Contains("Loading matches", StringComparison.Ordinal));
        }
        finally
        {
            ResetNetworkConditions();
        }

        Wait.Until(d => !d.PageSource.Contains("Loading matches", StringComparison.Ordinal));
    }

    // Scenario 12: a failed fetch shows a friendly error message with a working retry button, proven
    // with a real network failure (CDP offline emulation) rather than by inspecting component state.
    [Fact]
    public void MatchesPage_NetworkFailure_ShowsErrorStateWithWorkingRetry()
    {
        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");
        WaitForInitialPageLoad();

        SetNetworkConditions(offline: true, latencyMs: 0, throughputBytesPerSecond: 0);
        try
        {
            Driver.FindElement(By.XPath("//button[contains(.,'Refresh')]")).Click();
            var alertText = WaitForAlertText();
            Assert.False(string.IsNullOrWhiteSpace(alertText));
        }
        finally
        {
            ResetNetworkConditions();
        }

        // Find-and-click as one retried step: a plain find-then-click can race the alert panel's own
        // re-render (same reasoning as WaitForAlertText) and throw StaleElementReferenceException.
        Wait.Until(d =>
        {
            try
            {
                d.FindElement(By.XPath("//button[contains(.,'Retry')]")).Click();
                return true;
            }
            catch (StaleElementReferenceException)
            {
                return false;
            }
        });
        Wait.Until(d => d.FindElements(By.CssSelector("[role='alert']")).Count == 0);
    }

    // Definition of Done: the profile page's Possible Matches tile shows a live count (not a stuck
    // "Loading" or "unavailable" state) and links to the real Matches page.
    [Fact]
    public void ProfilePage_PossibleMatchesTile_ShowsLiveCountAndLinksToMatchesPage()
    {
        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/profile");

        // The h2's parent is the tile's own <Link to="/matched-items"> - the whole tile is one anchor,
        // not a <section> wrapping a separate nested link.
        var tile = Wait.Until(d => d.FindElement(By.XPath("//h2[normalize-space()='Possible Matches']/..")));
        Wait.Until(d => !tile.Text.Contains("Loading count", StringComparison.Ordinal));
        Assert.DoesNotContain("Count unavailable", tile.Text, StringComparison.Ordinal);
        Assert.Contains("View Matched Items", tile.Text, StringComparison.Ordinal);

        tile.Click();
        Wait.Until(d => d.Url.Contains("/matched-items", StringComparison.Ordinal));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    // Minimal claim-dialog drive: create/preview/submit only, no cancel or error branches - those are
    // already fully covered in ClaimAndMatchFlowTests. Deliberately duplicated rather than shared,
    // since this file only ever needs the one straight-through path to seed a real half-confirmed match.
    private void SubmitClaim(Guid targetFoundItemId, string candidateTitle)
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

    // Waits for a real, positive signal that the SPA has mounted and its initial request has
    // finished - the Refresh button existing - rather than for "Loading matches" text to be absent
    // from the page source. That negative check is also true of the raw pre-hydration HTML shell
    // (before React has rendered anything at all), so it can pass vacuously, immediately after
    // navigation, well before the page - or the Refresh button a caller is about to click - actually
    // exists.
    private void WaitForInitialPageLoad()
    {
        Wait.Until(d => d.FindElements(By.XPath("//button[contains(.,'Refresh')]")).Count > 0);
    }

    // Finds and reads the alert element as one atomic, retried step. A separate find-then-read (find
    // via Wait.Until, then read .Text afterward) can race a React re-render between the two and throw
    // StaleElementReferenceException even though the alert is, from the user's perspective, still there.
    private string WaitForAlertText()
    {
        var text = string.Empty;

        Wait.Until(d =>
        {
            try
            {
                text = d.FindElement(By.CssSelector("[role='alert']")).Text;
                return !string.IsNullOrWhiteSpace(text);
            }
            catch (StaleElementReferenceException)
            {
                return false;
            }
            catch (NoSuchElementException)
            {
                return false;
            }
        });

        return text;
    }

    private IWebElement SectionHeaderButton(string title) =>
        Driver.FindElement(By.XPath(
            $"//button[@aria-expanded and .//span[normalize-space()='{title}']]"));

    private void AssertSectionExpanded(string title, bool expected)
    {
        var actual = SectionHeaderButton(title).GetAttribute("aria-expanded") == "true";
        Assert.True(actual == expected, $"Expected section '{title}' aria-expanded to be {expected}.");
    }

    private void SetNetworkConditions(bool offline, int latencyMs, int throughputBytesPerSecond)
    {
        if (Driver is not ChromiumDriver chrome) return;

        chrome.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
        chrome.ExecuteCdpCommand("Network.emulateNetworkConditions", new Dictionary<string, object>
        {
            { "offline", offline },
            { "latency", latencyMs },
            { "downloadThroughput", throughputBytesPerSecond },
            { "uploadThroughput", throughputBytesPerSecond }
        });
    }

    private void ResetNetworkConditions()
    {
        if (Driver is not ChromiumDriver chrome) return;

        chrome.ExecuteCdpCommand("Network.emulateNetworkConditions", new Dictionary<string, object>
        {
            { "offline", false },
            { "latency", 0 },
            { "downloadThroughput", -1 },
            { "uploadThroughput", -1 }
        });
    }
}
