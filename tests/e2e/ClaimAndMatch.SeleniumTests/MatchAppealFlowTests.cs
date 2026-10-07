using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story LF-81: a reporter whose pair scored below the 60% threshold can send that pair to an admin
// for review, and is warned off editing either report while the appeal is waiting.
//
// The decision rules themselves (threshold boundary, note length, previewVersion 409, duplicate
// 409, contact-details 409, "can be claimed directly" 422) are already covered exhaustively at the
// xUnit level in AppealServiceTests, and the HTTP contract including both concurrency races is
// covered in AppealsApiIntegrationTests. None of those can prove the browser wiring, which is the
// only thing this file exists for:
//   - the Appeal button is reachable from the claim dialog and its disabled state tracks the score;
//   - the confirmation panel, note field and success message actually render and are clickable;
//   - "Appeal unavailable" is driven by the pair-status call rather than local state;
//   - the edit warning fires on the real edit page for a genuinely pending appeal, and stays
//     silent for a report with no appeal - the negative case is what proves the positive one.
[Trait("Story", "81")]
public sealed class MatchAppealFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private const string BelowThresholdLostDescription =
        "A small green kettle left behind on a wooden table in the reading room.";
    private const string BelowThresholdFoundDescription =
        "An orange bicycle with a black bell chained to a metal rack outside.";

    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public MatchAppealFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // The whole feature hinges on this split: a pair that cannot be claimed is exactly the pair
    // that can be appealed. Both halves run through the same real wizard-created reports, so the
    // only variable is the report text.
    [Fact]
    public void AppealButton_IsEnabled_WhenThePairScoresBelowTheThreshold()
    {
        var pair = CreateBelowThresholdPair();

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);

        var appeal = Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']")));
        Assert.True(
            appeal.Enabled,
            "Expected the Appeal button to be enabled for a pair below the 60% threshold.");

        // The dialog must also say why claiming is unavailable, or the enabled Appeal button
        // would be the only clue that this pair is appealable rather than claimable.
        Assert.Contains(
            "do not meet the similarity threshold",
            Driver.PageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppealButton_IsDisabled_WhenThePairScoresAtOrAboveTheThreshold()
    {
        // Identical title/category/description scores 100%, so the pair is claimable directly and
        // must not be appealable - appealing it would be asking an admin to redo a claim.
        var pair = CreateIdenticalPair("above");

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);

        var appeal = Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']")));
        Assert.False(
            appeal.Enabled,
            "Expected the Appeal button to be disabled for a pair at or above the 60% threshold.");

        // Sanity check that this pair really was claimable, so the assertion above is proving the
        // threshold rule rather than passing because the button never rendered enabled at all.
        Assert.Contains(
            "100.00%",
            Driver.PageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingAppeal_ShowsTheConfirmationPanelWithANoteField()
    {
        var pair = CreateBelowThresholdPair();

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);

        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();

        Wait.Until(d => d.PageSource.Contains(
            "Send this pair to an admin for review?", StringComparison.Ordinal));

        var note = Wait.Until(d => d.FindElement(By.Id("appeal-note")));
        Assert.Equal(string.Empty, note.GetAttribute("value"));

        // The note is optional but capped, and the counter is the only place that cap is visible.
        Assert.Equal("300", note.GetAttribute("maxlength"));
        Assert.Contains("0/300", Driver.PageSource, StringComparison.Ordinal);

        // The panel must warn about editing before the appeal is sent, not only after.
        Assert.Contains(
            "Do not edit your report while this appeal is",
            Driver.PageSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SendingAnAppeal_ShowsTheSuccessMessage_AndStoresItAsPending()
    {
        var pair = CreateBelowThresholdPair();
        const string note = "Selenium LF-81: these are the same kettle, please review.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();

        Wait.Until(d => d.FindElement(By.Id("appeal-note"))).SendKeys(note);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Send appeal']"))).Click();

        Wait.Until(d => d.PageSource.Contains("Appeal sent", StringComparison.Ordinal));
        Assert.Contains(
            "Appeal sent. An admin will review it.",
            Driver.PageSource,
            StringComparison.Ordinal);

        // The dialog offering a route to the appeals list is the only in-app path to the status
        // page, so it has to survive the state change to "sent".
        var myAppeals = Wait.Until(d => d.FindElement(
            By.XPath("//a[normalize-space()='View My appeals']")));
        Assert.Contains("/matched-items?tab=appeals", myAppeals.GetAttribute("href"), StringComparison.Ordinal);

        // Asserted against the row rather than the toast, so a green run cannot pass on the UI
        // alone if the POST silently failed.
        Assert.Equal("PENDING", _fixture.GetAppealStatus(pair.LostId, pair.FoundId));
    }

    [Fact]
    public void ReopeningAnAlreadyAppealedPair_ShowsTheAppealButtonAsUnavailable()
    {
        var pair = CreateBelowThresholdPair();

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Send appeal']"))).Click();
        Wait.Until(d => d.PageSource.Contains("Appeal sent", StringComparison.Ordinal));

        // Re-previewing the same pair must reflect server state, so the button changes label and
        // disables itself - this is what stops a user building up duplicate appeals by retrying.
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);

        var unavailable = Wait.Until(d => d.FindElement(
            By.XPath("//button[normalize-space()='Appeal unavailable']")));
        Assert.False(unavailable.Enabled, "Expected 'Appeal unavailable' to be disabled.");
    }

    [Fact]
    public void MyAppealsTab_ListsTheSentAppealWithItsStatusAndSentDate()
    {
        var pair = CreateBelowThresholdPair();
        const string note = "Selenium LF-81 my-appeals listing note.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();
        Wait.Until(d => d.FindElement(By.Id("appeal-note"))).SendKeys(note);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Send appeal']"))).Click();
        Wait.Until(d => d.FindElement(By.XPath("//a[normalize-space()='View My appeals']"))).Click();

        Wait.Until(d => d.Url.Contains("/matched-items", StringComparison.Ordinal));
        Wait.Until(d => !d.PageSource.Contains("Loading appeals", StringComparison.Ordinal));

        Assert.Equal($"{ClaimAndMatchFixture.BaseUrl}/matched-items?tab=appeals", Driver.Url);
        Assert.Contains(pair.LostTitle, Driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Sent ", Driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Score ", Driver.PageSource, StringComparison.Ordinal);

        // Scoped to this pair's own card: userA is a shared seeded account, so earlier tests in
        // this class legitimately have appeals listed too and a page-wide count would be flaky.
        // [1] picks the outermost match, because MatchItemCard is itself an <article> nested
        // inside the appeal's article, so this XPath matches both and the ancestor comes first.
        var card = Wait.Until(d => d.FindElement(
            By.XPath($"(//article[.//h3[normalize-space()='{pair.LostTitle}']])[1]")));
        Assert.Contains(pair.FoundTitle, card.Text, StringComparison.Ordinal);
        Assert.Contains("Pending", card.Text, StringComparison.Ordinal);
        Assert.Contains(note, card.Text, StringComparison.Ordinal);
        Assert.Contains("Sent ", card.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Decided ", card.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTheEditPageOfAnAppealedReport_ShowsTheMatchAppealWarning()
    {
        var pair = CreateBelowThresholdPair();

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Send appeal']"))).Click();
        Wait.Until(d => d.PageSource.Contains("Appeal sent", StringComparison.Ordinal));

        // The appellant editing their own report mid-review is the exact case the warning exists
        // for, so this is checked against the report that was actually appealed.
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/edit-lost-item/{pair.LostId}");

        Wait.Until(d => d.FindElement(By.Id("appeal-edit-warning-title")));
        Assert.Equal(
            "Part of a match appeal",
            Driver.FindElement(By.Id("appeal-edit-warning-title")).Text);
        Assert.Contains(
            "Saving changes may cancel the match permanently. Continue?",
            Driver.PageSource,
            StringComparison.Ordinal);

        // Cancelling must actually keep the user out of the form.
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Cancel']"))).Click();
        Wait.Until(d => !d.Url.Contains("/edit-lost-item/", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingAnEditOfAnAppealedReport_ShowsTheSecondReminder()
    {
        var pair = CreateBelowThresholdPair();

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(pair.FoundId);
        PreviewPair(pair.LostTitle);
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Appeal']"))).Click();
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Send appeal']"))).Click();
        Wait.Until(d => d.PageSource.Contains("Appeal sent", StringComparison.Ordinal));

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/edit-lost-item/{pair.LostId}");
        Wait.Until(d => d.FindElement(By.Id("appeal-edit-warning-title")));
        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Continue']"))).Click();

        Wait.Until(d => d.PageSource.Contains("Edit Lost Item Report", StringComparison.Ordinal));

        var titleField = Driver.FindElement(By.Id("title"));
        titleField.Clear();
        titleField.SendKeys($"{pair.LostTitle} edited");

        // Never pre-filled by design (EditLostItemPage), so it must be re-entered to save.
        Driver.FindElement(By.Id("hiddenInformation"))
            .SendKeys("Selenium LF-81 re-entered hidden information.");

        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Save Changes']"))).Click();

        // Second, distinct stage: acknowledging the warning on entry is not enough, the user has
        // to confirm again at the point the match could actually be cancelled.
        Wait.Until(d => d.FindElement(By.Id("appeal-edit-warning-title")));
        Assert.Equal(
            "Save these changes?",
            Driver.FindElement(By.Id("appeal-edit-warning-title")).Text);
        Assert.Contains(
            "saving may cancel the match permanently",
            Driver.PageSource,
            StringComparison.Ordinal);

        Wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Keep editing']"))).Click();

        // Backing out of the save leaves the user's edit in the form, unsaved - so "nothing was
        // written" has to be proven against the stored report, not the input's value.
        Wait.Until(d => d.PageSource.Contains("Edit Lost Item Report", StringComparison.Ordinal));
        Assert.Equal(
            pair.LostTitle,
            _fixture.GetItemTitle(pair.LostId, "LOST"));
        Assert.DoesNotContain("Changes saved.", Driver.PageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTheEditPageOfANonAppealedReport_ShowsNoWarning()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            "Selenium LF-81 no-warning control report",
            "Other",
            "A plain brown wallet that nobody has appealed.",
            "Test location",
            withPhoto: false,
            userAId);

        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/edit-lost-item/{lostId}");

        // Wait for the form itself, then prove no modal arrived - otherwise this would pass even if
        // the edit-warning request were simply still in flight.
        Wait.Until(d => d.PageSource.Contains("Edit Lost Item Report", StringComparison.Ordinal));
        Assert.Null(Driver.FindElements(By.Id("appeal-edit-warning-title")).FirstOrDefault());
        Assert.DoesNotContain("Part of a match appeal", Driver.PageSource, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------

    // Two real reports whose text shares almost nothing, so the pair scores well under the 60%
    // threshold and is therefore appealable. Every call mints a fresh title tag: the claim dialog
    // lists all of the user's active reports and the tests select a candidate by its title, so
    // repeated calls with one fixed title would leave several identical rows to pick between and
    // could silently preview this call's found report against an earlier call's lost report.
    private (Guid LostId, Guid FoundId, string LostTitle, string FoundTitle) CreateBelowThresholdPair()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var lostTitle = $"Selenium LF-81 {tag} green kettle";
        var foundTitle = $"Selenium LF-81 {tag} orange bicycle";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            lostTitle,
            "Other",
            BelowThresholdLostDescription,
            "Central Library",
            withPhoto: false,
            userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            foundTitle,
            "Other",
            BelowThresholdFoundDescription,
            "City Gym",
            withPhoto: false,
            userBId);

        return (lostId, foundId, lostTitle, foundTitle);
    }

    private (Guid LostId, Guid FoundId, string LostTitle) CreateIdenticalPair(string label)
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        var title = $"Selenium LF-81 {label}-{Guid.NewGuid():N} teal bicycle helmet";
        const string description = "A teal bicycle helmet with a single black strap.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, "Other", description, "Main Hall", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, "Other", description, "Main Hall", withPhoto: false, userBId);

        return (lostId, foundId, title);
    }

    // UserA owns the lost report and is browsing UserB's found report, which is the only direction
    // the claim dialog offers an appeal from when the lost reporter is the one appealing.
    private void OpenClaimDialog(Guid foundItemId)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/items/{foundItemId}");
        Wait.Until(d => d.FindElement(
            By.XPath("//button[contains(.,'This Is My Lost Item')]"))).Click();

        Wait.Until(d => d.FindElement(
            By.CssSelector("dialog[aria-labelledby='claim-dialog-title']")));
        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));
    }

    private void PreviewPair(string candidateTitle)
    {
        Driver.FindElement(By.XPath(
            $"//button[.//span[normalize-space()='{candidateTitle}']]")).Click();
        Wait.Until(d => d.PageSource.Contains("Similarity score", StringComparison.Ordinal));
    }
}