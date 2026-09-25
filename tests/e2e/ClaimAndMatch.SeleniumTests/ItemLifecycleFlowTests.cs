using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 7: resolving/deleting a report through My Reports, and the resulting Deactivated state on
// the shared review screen. Scenario 4 (claim blocked by an inactive item) isn't retested here:
// ItemDetailsPage 404s a resolved item outright, unreachable through the real UI - already covered
// at the xUnit level (ClaimServiceTests).
[Trait("Story", "7")]
public sealed class ItemLifecycleFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public ItemLifecycleFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenario 1: resolving the lost reporter's own report deactivates the pending match.
    [Fact]
    public void ResolvingTheLostItem_DeactivatesThePendingMatch_ShowsReportResolvedOnReviewScreen()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story7 resolve lost umbrella";
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
        ResolveOwnReport(title);

        WaitForMatchDeactivation(matchId);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Report resolved", StringComparison.Ordinal));

        Assert.Contains("This match is read-only", Driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("confirmation and rejection are unavailable", Driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    // Scenario 2 mirror: deleting the finder's own report deactivates the match with its own reason.
    [Fact]
    public void DeletingTheFoundItem_DeactivatesThePendingMatch_ShowsReportDeletedOnReviewScreen()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story7 delete found sandal";
        const string category = "Other";
        const string description = "A single blue rubber sandal, size 8.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);
        SubmitClaimAsFinder(lostId, title);
        var matchId = _fixture.GetLatestMatchId(userBId);

        DeleteOwnReport(title);

        WaitForMatchDeactivation(matchId);
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Report deleted", StringComparison.Ordinal));
    }

    // Scenario 3: confirming a match auto-resolves both items (ConfirmedMatchHandler, Item Service),
    // but the confirmed match itself stays untouched.
    [Fact]
    public void ConfirmingAMatch_AutoResolvesBothItems_ButLeavesTheMatchUntouched()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium story7 confirmed protected backpack";
        const string category = "Bags";
        const string description = "A green hiking backpack with a reflective strip.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
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

        WaitForItemToBecomeInactive(lostId, "LOST");
        WaitForItemToBecomeInactive(foundId, "FOUND");

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/matched-items/{matchId}");
        Wait.Until(d => d.PageSource.Contains("Match confirmed by both parties", StringComparison.Ordinal));
        Assert.DoesNotContain("Report resolved", Driver.PageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("This match is read-only", Driver.PageSource, StringComparison.Ordinal);
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

    private void ResolveOwnReport(string title)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/my-reports");
        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        var card = Wait.Until(d => d.FindElement(By.XPath($"//p[normalize-space()='{title}']/ancestor::div[contains(@class,'rounded-2xl')][1]")));
        card.FindElement(By.XPath(".//button[contains(.,'Mark as Resolved')]")).Click();

        Wait.Until(d => d.FindElement(By.Id("resolve-modal-title")));
        Driver.FindElement(By.XPath("//button[contains(.,'Yes, Resolve')]")).Click();
        Wait.Until(d => d.FindElements(By.Id("resolve-modal-title")).Count == 0);
    }

    private void DeleteOwnReport(string title)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/my-reports");
        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        var card = Wait.Until(d => d.FindElement(By.XPath($"//p[normalize-space()='{title}']/ancestor::div[contains(@class,'rounded-2xl')][1]")));
        card.FindElement(By.CssSelector("button[aria-label='Delete report']")).Click();

        Wait.Until(d => d.FindElement(By.Id("delete-modal-title")));
        Driver.FindElement(By.XPath("//button[contains(.,'Yes, Delete')]")).Click();
        Wait.Until(d => d.FindElements(By.Id("delete-modal-title")).Count == 0);
    }

    // Real Kafka consumer, polled directly rather than assumed synchronous.
    private void WaitForMatchDeactivation(Guid matchId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_fixture.GetMatchDeactivationReason(matchId) is not null) return;
            Thread.Sleep(1000);
        }

        throw new Xunit.Sdk.XunitException($"Match {matchId} was never deactivated within 90s.");
    }

    private void WaitForItemToBecomeInactive(Guid itemId, string itemType)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_fixture.IsItemInactive(itemId, itemType)) return;
            Thread.Sleep(1000);
        }

        throw new Xunit.Sdk.XunitException($"{itemType} item {itemId} never became inactive within 90s.");
    }
}
