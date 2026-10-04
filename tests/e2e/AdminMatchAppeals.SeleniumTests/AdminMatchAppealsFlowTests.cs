using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace AdminMatchAppeals.SeleniumTests;

// Story LF-338: an admin works through the match appeal queue - list by status, open an appeal to
// see what the pair scores now, then verify (which creates the match) or reject with a reason.
//
// The decision rules themselves are already covered exhaustively at the xUnit level in
// AdminAppealServiceTests, and the HTTP contract including both concurrency races is covered in
// AdminAppealsApiIntegrationTests. None of those touch a browser, so this file exists only for the
// parts that do:
//
//   - the queue actually filters by status and renders each appeal's reports, contacts and score;
//   - "Open" fetches a fresh score and shows it next to the score recorded when the appeal was sent;
//   - Verify and Reject are behind a confirmation step, and both remove the card from Pending;
//   - the rejection reason an admin types is what the appellant is later shown;
//   - Verify is refused in the UI when one of the two reporters' accounts no longer exists.
//
// Every assertion is scoped to one seeded appeal's card rather than the whole page, because the
// admin list is shared, accumulates across this class, and already contains appeals from other
// suites and from earlier runs.
[Trait("Story", "338")]
public sealed class AdminMatchAppealsFlowTests : IClassFixture<AdminAppealsFixture>
{
    private readonly AdminAppealsFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public AdminMatchAppealsFlowTests(AdminAppealsFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PendingAppeals_AreListedWithBothReports_TheScoreSent_AndTheContacts()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} seeded teal lantern";
        var note = $"Selenium LF-338 {tag} listing note.";

        _fixture.EnsureAdminSession();
        _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} seeded brass compass",
            note);

        GoToAppeals();

        var card = CardFor(lostTitle);
        Assert.Contains("Pending", card.Text, StringComparison.Ordinal);
        Assert.Contains(note, card.Text, StringComparison.Ordinal);

        // Both sides of the pair, the score recorded at send time, and both reporters' contacts -
        // an admin cannot judge an appeal without all four.
        Assert.Contains(
            $"Selenium LF-338 {tag} seeded brass compass",
            card.Text,
            StringComparison.Ordinal);
        Assert.Contains("Score when sent:", card.Text, StringComparison.Ordinal);
        Assert.Contains(AdminAppealsFixture.UserAEmail, card.Text, StringComparison.Ordinal);
        Assert.Contains(AdminAppealsFixture.UserBEmail, card.Text, StringComparison.Ordinal);
        Assert.Contains("LOST REPORTER", card.Text, StringComparison.Ordinal);
        Assert.Contains("FINDER", card.Text, StringComparison.Ordinal);

        // The per-component breakdown is what justifies the score, so it has to be on the card
        // without opening anything.
        Assert.Contains("Title", card.Text, StringComparison.Ordinal);
        Assert.Contains("Description", card.Text, StringComparison.Ordinal);

        Assert.True(
            card.FindElement(By.XPath(".//button[normalize-space()='Verify']")).Enabled,
            "Expected Verify to be available while both reporters' accounts exist.");
    }

    [Fact]
    public void StatusTabs_FilterTheQueueToTheChosenStatus()
    {
        var tag = NewTag();
        var pendingTitle = $"Selenium LF-338 {tag} tab pending grey crate";
        var rejectedTitle = $"Selenium LF-338 {tag} tab rejected blue crate";

        _fixture.EnsureAdminSession();
        _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            pendingTitle,
            $"Selenium LF-338 {tag} tab pending green crate");

        _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "REJECTED",
            rejectedTitle,
            $"Selenium LF-338 {tag} tab rejected orange crate",
            rejectionReason: "Already matched through another pair.");

        GoToAppeals();
        Assert.NotNull(CardFor(pendingTitle));

        ClickTab("Rejected");
        var rejectedCard = CardFor(rejectedTitle);
        Assert.Contains("Rejected", rejectedCard.Text, StringComparison.Ordinal);
        Assert.Contains("Already matched through another pair.", rejectedCard.Text, StringComparison.Ordinal);

        // The decisive assertion: a decided appeal must not still be actionable from Pending.
        ClickTab("Pending");
        Assert.Null(FindCard(rejectedTitle));
        Assert.NotNull(CardFor(pendingTitle));

        // Only PENDING appeals carry the decision buttons at all.
        ClickTab("Rejected");
        Assert.Null(ButtonInCard(rejectedTitle, "Verify"));
        Assert.Null(ButtonInCard(rejectedTitle, "Reject"));
    }

    [Fact]
    public void OpeningAPendingAppeal_ShowsTheCurrentScoreBesideTheScoreItWasSentWith()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} open violet backpack";

        // Real reports, because OpenAsync re-reads both from Item Service and reports
        // "no longer active" for anything it cannot load.
        var pair = _fixture.CreateReportPairViaApi(
            lostTitle,
            $"Selenium LF-338 {tag} open yellow backpack",
            "A violet backpack with one strap and a zip pocket.",
            "A yellow backpack with two straps and no zip pocket.");

        _fixture.EnsureAdminSession();
        _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} open yellow backpack",
            lostItemId: pair.LostItemId,
            foundItemId: pair.FoundItemId);

        GoToAppeals();
        ClickInCard(lostTitle, "Open");

        var card = CardFor(lostTitle);
        Assert.Contains("Current score:", card.Text, StringComparison.Ordinal);

        // Both scores stay on screen together - comparing what the pair scored now against what it
        // scored when the appeal was sent is the whole point of opening it.
        Assert.Contains("Score when sent:", card.Text, StringComparison.Ordinal);
        Assert.Contains("Title", card.Text, StringComparison.Ordinal);
        Assert.Contains("Category", card.Text, StringComparison.Ordinal);

        // A second look is offered rather than silently reusing the first result.
        Assert.Equal(
            "Recheck score",
            card.FindElement(By.XPath(".//button[normalize-space()='Recheck score']")).Text);
    }

    [Fact]
    public void VerifyAppeal_AsksForConfirmation_ThenCreatesTheMatchAndClearsTheQueue()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} verify crimson jacket";

        var pair = _fixture.CreateReportPairViaApi(
            lostTitle,
            $"Selenium LF-338 {tag} verify magenta jacket",
            "A crimson jacket left on a chair.",
            "A magenta jacket left on a bench.");

        var appealId = _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} verify magenta jacket",
            lostItemId: pair.LostItemId,
            foundItemId: pair.FoundItemId);

        _fixture.EnsureAdminSession();
        GoToAppeals();
        ClickInCard(lostTitle, "Verify");

        // Verification creates a match for real, so it must be an explicit two-step action.
        var card = CardFor(lostTitle);
        Assert.Contains("Create a match for this pair?", card.Text, StringComparison.Ordinal);
        Assert.True(ButtonInCard(lostTitle, "Yes, verify")!.Enabled);

        ClickInCard(lostTitle, "Yes, verify");
        Wait.Until(d => FindCard(lostTitle) == null);

        // Asserted against the rows, not the screen: the appeal being marked Verified is not the
        // same claim as the match having been created.
        Assert.Equal("VERIFIED", _fixture.GetAppealStatus(appealId));
        Assert.NotNull(_fixture.GetMatchIdForPair(pair.LostItemId, pair.FoundItemId));

        ClickTab("Verified");
        var verifiedCard = CardFor(lostTitle);
        Assert.Contains("Verified", verifiedCard.Text, StringComparison.Ordinal);

        // Verified is terminal in the UI - the decision buttons are gone, not merely disabled.
        Assert.Null(ButtonInCard(lostTitle, "Verify"));
        Assert.Null(ButtonInCard(lostTitle, "Reject"));
    }

    [Fact]
    public void RejectAppeal_WithAReason_ShowsThatReasonOnTheRejectedTab()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} reject teal flask";
        const string reason = "The finder already returned this item to its owner.";

        var appealId = _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} reject amber flask");

        _fixture.EnsureAdminSession();
        GoToAppeals();
        ClickInCard(lostTitle, "Reject");

        var card = CardFor(lostTitle);
        Assert.Contains("Reject this appeal?", card.Text, StringComparison.Ordinal);

        // The reason is optional and explicitly labelled as such, and the counter shows the cap.
        var textarea = Wait.Until(d => d.FindElement(By.Id($"reject-reason-{appealId}")));
        Assert.Equal("300", textarea.GetAttribute("maxlength"));
        Assert.Contains("optional", card.Text, StringComparison.Ordinal);

        textarea.SendKeys(reason);
        ClickInCard(lostTitle, "Yes, reject");
        Wait.Until(d => FindCard(lostTitle) == null);

        Assert.Equal("REJECTED", _fixture.GetAppealStatus(appealId));
        Assert.Equal(reason, _fixture.GetRejectionReason(appealId));

        ClickTab("Rejected");
        var rejectedCard = CardFor(lostTitle);
        Assert.Contains(reason, rejectedCard.Text, StringComparison.Ordinal);

        // Case-insensitive because the label carries Tailwind's `uppercase`, so Selenium's rendered
        // .Text is "REASON GIVEN TO THE USER" even though the markup is mixed case.
        Assert.Contains(
            "Reason given to the user",
            rejectedCard.Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectAppeal_WithoutAReason_IsAllowed()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} reject no reason wallet";

        var appealId = _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} reject no reason purse");

        _fixture.EnsureAdminSession();
        GoToAppeals();
        ClickInCard(lostTitle, "Reject");

        // Deliberately left empty: rejecting must not be blocked by the optional reason.
        Wait.Until(d => d.FindElement(By.Id($"reject-reason-{appealId}")));
        ClickInCard(lostTitle, "Yes, reject");
        Wait.Until(d => FindCard(lostTitle) == null);

        Assert.Equal("REJECTED", _fixture.GetAppealStatus(appealId));
        Assert.Null(_fixture.GetRejectionReason(appealId));

        ClickTab("Rejected");
        var card = CardFor(lostTitle);
        Assert.Contains("Rejected", card.Text, StringComparison.Ordinal);

        // Same case-insensitive caveat as the with-reason test: this is only a meaningful negative
        // if it would actually have matched the label had a reason been recorded.
        Assert.DoesNotContain(
            "Reason given to the user",
            card.Text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyButton_IsDisabled_WhenOneOfTheReportersNoLongerExists()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} deleted user satchel";

        // A reporter id that is in no user's table is exactly what Auth Service answers 404 for,
        // which is how the page learns the account is gone.
        _fixture.SeedAppeal(
            Guid.NewGuid(),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} deleted user pouch");

        _fixture.EnsureAdminSession();
        GoToAppeals();

        var card = CardFor(lostTitle);
        Assert.Contains("Deleted user", card.Text, StringComparison.Ordinal);

        var verify = card.FindElement(By.XPath(".//button[normalize-space()='Verify']"));
        Assert.False(
            verify.Enabled,
            "Expected Verify to be disabled when one of the two reporters' accounts no longer exists.");

        // Rejecting must still be possible - an appeal nobody can act on would be a dead end.
        Assert.True(
            card.FindElement(By.XPath(".//button[normalize-space()='Reject']")).Enabled,
            "Expected Reject to stay available when a reporter's account no longer exists.");
    }

    [Fact]
    public void OpeningAnAppealWhoseReportsAreNoLongerActive_SaysSoInsteadOfAScore()
    {
        var tag = NewTag();
        var lostTitle = $"Selenium LF-338 {tag} inactive red folder";

        // No real reports behind these ids, so OpenAsync takes its REPORT_INACTIVE branch. The
        // admin must be told the reports are gone rather than shown a stale or invented score.
        _fixture.SeedAppeal(
            _fixture.GetUserId(AdminAppealsFixture.UserAEmail),
            _fixture.GetUserId(AdminAppealsFixture.UserBEmail),
            "PENDING",
            lostTitle,
            $"Selenium LF-338 {tag} inactive blue folder");

        _fixture.EnsureAdminSession();
        GoToAppeals();
        ClickInCard(lostTitle, "Open");

        var card = CardFor(lostTitle);
        Assert.Contains("Report no longer active", card.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Current score:", card.Text, StringComparison.Ordinal);

        // The appeal is still decidable as a rejection, so Open failing must not disable Reject.
        Assert.True(
            card.FindElement(By.XPath(".//button[normalize-space()='Reject']")).Enabled,
            "Expected Reject to remain available when the reports are no longer active.");
    }

    // ---- Helpers -----------------------------------------------------------

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    private void GoToAppeals()
    {
        Driver.Navigate().GoToUrl($"{AdminAppealsFixture.BaseUrl}/admin/match-appeals");
        Wait.Until(d => d.FindElement(By.XPath("//h1[normalize-space()='Match Appeals']")));
        WaitForListToLoad();
    }

    // The queue shows "Loading appeals…" only on a cold load; switching tabs re-fetches without it,
    // so waiting for the absence of both loading states keeps the tab helpers interchangeable.
    private void WaitForListToLoad()
    {
        Wait.Until(d => !d.PageSource.Contains("Loading appeals", StringComparison.Ordinal));
    }

    private void ClickTab(string label)
    {
        Wait.Until(d => d.FindElement(By.XPath($"//button[@role='tab'][normalize-space()='{label}']"))).Click();
        WaitForListToLoad();
    }

    // MatchItemCard is itself an <article> nested inside the appeal's article, so this XPath also
    // matches the inner cards; [1] takes the outermost, which is the appeal itself.
    private IWebElement? FindCard(string lostTitle) =>
        Driver.FindElements(
            By.XPath($"(//article[.//h3[normalize-space()='{lostTitle}']])[1]"))
            .FirstOrDefault();

    private IWebElement CardFor(string lostTitle)
    {
        var card = Wait.Until(d => FindCard(lostTitle));
        if (card is null)
        {
            throw new InvalidOperationException(
                $"No appeal card for '{lostTitle}'. Queue text: {Driver.FindElement(By.TagName("body")).Text}");
        }

        return card;
    }

    // FindElements, not FindElement: a button that is absent is a real assertion target here (a
// decided appeal must no longer offer decision buttons), and FindElement throws instead of
// returning null, which would abort the test before the assertion could run.
private IWebElement? ButtonInCard(string lostTitle, string buttonText) =>
        FindCard(lostTitle)?
            .FindElements(By.XPath($".//button[normalize-space()='{buttonText}']"))
            .FirstOrDefault();

    private void ClickInCard(string lostTitle, string buttonText)
    {
        // Re-resolved every time: React replaces the button row on each state change, so a
        // reference captured before the click goes stale.
        Wait.Until(d => ButtonInCard(lostTitle, buttonText)!).Click();
    }
}