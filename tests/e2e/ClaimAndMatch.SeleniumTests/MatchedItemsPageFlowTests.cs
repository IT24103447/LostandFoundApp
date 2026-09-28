using OpenQA.Selenium;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 3: the Matches page, the review screen's read-only states, ownership isolation, and the
// profile page's live-count tile. Confirm/Reject is Story 4/5's own coverage. Matches needing a
// status no real endpoint produces are seeded via ClaimAndMatchFixture.InsertMatchDirectly.
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

    // Scenarios 2 + 4: a row shows the other party's item and confidence collapsed; expanding it
    // reveals more detail, and the review link opens the shared review screen.
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

        var cardXPath = By.XPath($"//article[.//p[normalize-space()='{title}']]");
        Wait.Until(d => d.FindElement(cardXPath));

        var cardText = Driver.FindElement(cardXPath).Text;
        Assert.Contains("You submitted this claim", cardText, StringComparison.Ordinal);
        Assert.Contains("Similarity", cardText, StringComparison.Ordinal);

        Driver.FindElement(cardXPath).FindElement(By.XPath(".//button[contains(.,'View details')]")).Click();
        Wait.Until(d => d.FindElement(cardXPath).Text.Contains(category, StringComparison.Ordinal));
        Assert.Contains("Test location", Driver.FindElement(cardXPath).Text, StringComparison.Ordinal);

        Driver.FindElement(cardXPath).FindElement(By.XPath($".//a[@href='/matched-items/{matchId}']")).Click();
        Wait.Until(d => d.Url.Contains($"/matched-items/{matchId}", StringComparison.Ordinal));
        Wait.Until(d => d.PageSource.Contains(title, StringComparison.Ordinal));
    }

    // Scenario 6: the claimant who already confirmed sees read-only waiting text, never Confirm/Reject.
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
            "You confirmed your claim. The other person needs to review it.",
            StringComparison.Ordinal));

        Assert.Empty(Driver.FindElements(By.XPath("//button[contains(.,'Confirm')]")));
        Assert.Empty(Driver.FindElements(By.XPath("//button[contains(.,'Reject')]")));
    }

    // Scenario 3: matches group into all four sections; Rejected starts collapsed, the rest expanded.
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

        Wait.Until(d => d.PageSource.Contains("Selenium seeded confirmed lost", StringComparison.Ordinal));

        AssertSectionExpanded("Waiting On You", true);
        AssertSectionExpanded("Waiting On The Other Party", true);
        AssertSectionExpanded("Confirmed", true);
        AssertSectionExpanded("Rejected", false);

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

    // Scenario 8: auto-rejected and deactivated matches never appear, even in an expanded Rejected.
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

        Wait.Until(d => d.PageSource.Contains(
            "Review claims linked to your lost and found reports.", StringComparison.Ordinal));
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

    // Scenario 10: a fresh account with no matches sees the empty-state message.
    [Fact]
    public void MatchesPage_FreshAccountWithNoMatches_ShowsEmptyStateMessage()
    {
        var (_, email, password) = _fixture.GetOrRegisterFreshVerifiedUser();
        _fixture.LoginAs(email, password);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items");

        Wait.Until(d => d.PageSource.Contains("No potential matches yet", StringComparison.Ordinal));
    }

    // Scenario 11: a loading indicator shows while the page's request is in flight.
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

    // Scenario 12: a failed fetch shows an error message with a working retry button.
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

    // DoD: the profile page's Possible Matches tile shows a live count and links to the Matches page.
    [Fact]
    public void ProfilePage_PossibleMatchesTile_ShowsLiveCountAndLinksToMatchesPage()
    {
        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/profile");

        var tile = Wait.Until(d => d.FindElement(By.XPath("//h2[normalize-space()='Possible Matches']/..")));
        Wait.Until(d => !tile.Text.Contains("Loading count", StringComparison.Ordinal));
        Assert.DoesNotContain("Count unavailable", tile.Text, StringComparison.Ordinal);
        Assert.Contains("View Matched Items", tile.Text, StringComparison.Ordinal);

        tile.Click();
        Wait.Until(d => d.Url.Contains("/matched-items", StringComparison.Ordinal));
    }

    // ---- Helpers -----------------------------------------------------------------------------

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

    // Waits for the Refresh button to exist, a positive signal the SPA has mounted.
    private void WaitForInitialPageLoad()
    {
        Wait.Until(d => d.FindElements(By.XPath("//button[contains(.,'Refresh')]")).Count > 0);
    }

    // Finds and reads the alert element as one atomic, retried step.
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

    // The title span's own text also carries the section's count badge once loaded (e.g. "Waiting On
    // You" + "7"), so this matches on contains(), not an exact normalize-space() equality.
    private IWebElement SectionHeaderButton(string title) =>
        Wait.Until(d => d.FindElement(By.XPath(
            $"//button[@aria-expanded and .//span[contains(normalize-space(), '{title}')]]")));

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
