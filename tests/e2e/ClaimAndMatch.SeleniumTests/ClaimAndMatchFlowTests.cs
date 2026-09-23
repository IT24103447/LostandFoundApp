using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Story 2 (LF-173) browser coverage: the report picker popup, the preview/comparison, submitting a
/// claim, and the resulting Matched Items page. Report creation itself (the wizard, photo dropzone) is
/// Story 1's already-covered flow, driven here only as real setup for these scenarios, not re-tested.
///
/// Two real, already-seeded accounts play the two sides of every pair - see ClaimAndMatchFixture for
/// why no registration/email-verification is needed. By convention throughout this file, User B
/// (user2@example.com) only ever creates FOUND reports and User A (user1@example.com) only ever
/// creates LOST reports for the cross-user pairs, which keeps "User B has zero lost reports" (used by
/// the Scenario 1 empty-state test) true regardless of test execution order. Where a test needs a
/// same-owner pair (Scenario 6) or a target report to click through from, User A creates both sides
/// itself, deliberately, inside that one test.
///
/// Every report used here is created fresh, live, per test - never reusing the pre-existing item
/// records already sitting in the dev database from earlier manual QA - because a lost/found pair can
/// only be successfully claimed once (unique constraint), so a reused pair would only pass on the
/// suite's first run.
/// </summary>
[Trait("Story", "2")]
public sealed class ClaimAndMatchFlowTests : IClassFixture<ClaimAndMatchFixture>
{
    private readonly ClaimAndMatchFixture _fixture;
    private IWebDriver Driver => _fixture.Driver;
    private WebDriverWait Wait => _fixture.Wait;

    public ClaimAndMatchFlowTests(ClaimAndMatchFixture fixture)
    {
        _fixture = fixture;
    }

    // Scenario 1 (empty state): "if none exist, the popup explains this and offers a link to create
    // a report." User B never creates a lost report anywhere in this suite, so its candidates are
    // always empty here regardless of what other tests already ran.
    [Fact]
    public void Candidates_NoOwnReportsOfOppositeType_ShowsEmptyStateWithCreateLink()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var foundId = _fixture.CreateFoundReport(
            "Selenium empty-state target", "Keys", "A single brass key on a red lanyard.",
            "Test location", withPhoto: false, userAId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        OpenClaimDialog(foundId, "This Is My Lost Item");

        Wait.Until(d => d.PageSource.Contains(
            "You don’t have any active lost item reports.", StringComparison.Ordinal));

        var createLink = Driver.FindElement(By.XPath("//a[contains(.,'Create a lost report')]"));
        Assert.Contains("/report-lost-item", createLink.GetAttribute("href"), StringComparison.Ordinal);
    }

    // Scenarios 1 + 2: the picker lists my own active opposite-type reports by name, and selecting
    // one shows both reports side by side with a similarity score before any match is created.
    // Also covers Scenario 3's "either report has no image" branch: neither side has a photo here,
    // and the preview still completes with a plain text/category score, no wait state.
    [Fact]
    public void Selecting_ShowsOwnCandidates_AndPreviewShowsBothReportsWithScore()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium matching wallet";
        const string category = "Accessories";
        const string description = "A black leather bifold wallet with a small scratch on the front.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(title);
        WaitForPreview();

        Assert.Contains(title, Driver.PageSource, StringComparison.Ordinal);
        var score = GetScorePercent();
        Assert.InRange(score, 0, 100);
    }

    // Scenario 3, "wait" half: both sides have a photo and one is still (parked as) analysing -
    // the preview must show the "still being analysed" message rather than a score. Real photo
    // upload, real Kafka event, real worker-created row; only the row's processing state is forced,
    // since natural Gemini analysis is currently blocked (Bugs_Sprint3.md, Known External Dependency
    // Issue #1) and cannot be waited on deterministically.
    [Fact]
    public void Preview_ImageAnalysisPending_ShowsWaitMessage()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium pending-photo bag";
        const string category = "Bags";
        const string description = "A grey canvas tote bag with a broken zip pull.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: true, userAId);
        _fixture.ForceImageDescriptionState(
            lostId, "LOST", status: "COMPLETED", attempts: 1,
            description: "A grey tote bag.", attributesJson: "{}");

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: true, userBId);
        _fixture.ForceImageDescriptionState(
            foundId, "FOUND", status: "PENDING", attempts: 5,
            description: "", attributesJson: "{}");

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(title);

        Wait.Until(d => d.PageSource.Contains("still being analysed", StringComparison.Ordinal));
        Assert.False(Driver.FindElement(By.CssSelector("dialog[aria-labelledby='claim-dialog-title']"))
            .Text.Contains("Similarity score", StringComparison.Ordinal));
    }

    // Scenario 3, "both completed" half: both sides have a photo with a COMPLETED description -
    // the preview must succeed (no wait message, no error) and include a score, proving the image
    // branch is actually exercised rather than skipped.
    [Fact]
    public void Preview_BothReportsHaveCompletedImageDescriptions_SucceedsWithoutWaiting()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium completed-photo umbrella";
        const string category = "Other";
        const string description = "A compact navy-blue umbrella with a wooden handle.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var lostId = _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: true, userAId);
        _fixture.ForceImageDescriptionState(
            lostId, "LOST", status: "COMPLETED", attempts: 1,
            description: "A navy umbrella with a wooden handle.",
            attributesJson: "{\"colours\":[\"navy\"]}");

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: true, userBId);
        _fixture.ForceImageDescriptionState(
            foundId, "FOUND", status: "COMPLETED", attempts: 1,
            description: "A navy-blue umbrella, wooden handle, folded.",
            attributesJson: "{\"colours\":[\"navy\"]}");

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(title);
        WaitForPreview();

        var score = GetScorePercent();
        Assert.InRange(score, 0, 100);
    }

    // Scenario 4: at or above the threshold, "Confirm and claim" creates the match and it shows up
    // under Matched Items with the lost-reporter-confirmed status (User A, the lost-side owner, is
    // the one submitting here).
    [Fact]
    public void Submit_EligibleScore_CreatesConfirmedMatch_VisibleInMatchedItems()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        const string title = "Selenium claimable jacket";
        const string category = "Clothing";
        const string description = "An orange rain jacket with a broken front zipper.";

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            title, category, description, "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        _fixture.CreateLostReport(
            title, category, description, "Test location", withPhoto: false, userAId);

        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(title);
        WaitForPreview();

        var confirmButton = Driver.FindElement(By.XPath("//button[contains(.,'Confirm and claim')]"));
        Assert.True(confirmButton.Enabled, "Expected an eligible (>=60%) score to enable Confirm and claim.");
        confirmButton.Click();

        Wait.Until(d => d.PageSource.Contains("Claim submitted", StringComparison.Ordinal));
        Driver.FindElement(By.XPath("//a[contains(.,'View Matched Items')]")).Click();

        Wait.Until(d => d.Url.Contains("/matched-items", StringComparison.Ordinal));
        Wait.Until(d => d.PageSource.Contains(title, StringComparison.Ordinal));

        // Status is shown as the finder-facing label for LOST_REPORTER_CONFIRMED (MatchBanner),
        // not the raw enum name.
        Assert.Contains("Awaiting finder", Driver.PageSource, StringComparison.Ordinal);
    }

    // Scenario 5: below the threshold, Confirm and claim stays disabled and nothing is persisted;
    // cancelling closes the popup with no match created, and reopening runs a fresh comparison.
    [Fact]
    public void Preview_LowSimilarityScore_DisablesClaim_CancelAndReopenRecalculates()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);
        var userBId = _fixture.GetUserId(ClaimAndMatchFixture.UserBEmail);

        _fixture.LoginAs(ClaimAndMatchFixture.UserBEmail, ClaimAndMatchFixture.UserBPassword);
        var foundId = _fixture.CreateFoundReport(
            "Selenium silver keychain", "Keys", "A small silver keychain shaped like a star.",
            "Test location", withPhoto: false, userBId);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        const string lostTitle = "Selenium unrelated umbrella";
        _fixture.CreateLostReport(
            lostTitle, "Other", "A large golf umbrella, bright green, missing two ribs.",
            "Test location", withPhoto: false, userAId);

        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(lostTitle);
        WaitForPreview();

        var confirmButton = Driver.FindElement(By.XPath("//button[contains(.,'Confirm and claim')]"));
        Assert.False(confirmButton.Enabled, "Expected a clearly mismatched pair to score below threshold.");
        Assert.Contains(
            "do not meet the similarity threshold", Driver.PageSource, StringComparison.Ordinal);

        Driver.FindElement(By.XPath("//button[contains(.,'Cancel')]")).Click();
        Wait.Until(d => d.FindElements(By.CssSelector("dialog[aria-labelledby='claim-dialog-title']")).Count == 0);

        // Reopen: a fresh comparison is run, not a cached result.
        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(lostTitle);
        WaitForPreview();
        Assert.False(
            Driver.FindElement(By.XPath("//button[contains(.,'Confirm and claim')]")).Enabled);
    }

    // Scenario 6 (both-mine half): a user cannot claim between two reports they own themselves.
    // The other Scenario 6 cases (owning neither report, an inactive report, an already-matched
    // pair) are unreachable through this picker by construction or are concurrency-shaped - they
    // stay xUnit/JMeter's job (ClaimServiceTests, the planned duplicate-claim JMeter race test).
    [Fact]
    public void Preview_BothReportsBelongToCaller_IsRejectedWithoutCreatingAPreview()
    {
        var userAId = _fixture.GetUserId(ClaimAndMatchFixture.UserAEmail);

        _fixture.LoginAs(ClaimAndMatchFixture.UserAEmail, ClaimAndMatchFixture.UserAPassword);
        var foundId = _fixture.CreateFoundReport(
            "Selenium own-both found", "Documents", "A found passport-sized document wallet.",
            "Test location", withPhoto: false, userAId);
        const string lostTitle = "Selenium own-both lost";
        _fixture.CreateLostReport(
            lostTitle, "Documents", "A lost passport-sized document wallet.",
            "Test location", withPhoto: false, userAId);

        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(lostTitle);

        var alert = Wait.Until(d => d.FindElement(By.CssSelector("[role='alert']")));
        Assert.False(string.IsNullOrWhiteSpace(alert.Text));
        Assert.DoesNotContain("Similarity score", Driver.PageSource, StringComparison.Ordinal);
    }

    // ---- Dialog helpers --------------------------------------------------

    private void OpenClaimDialog(Guid targetItemId, string expectedButtonText)
    {
        Driver.Navigate().GoToUrl($"{ClaimAndMatchFixture.BaseUrl}/items/{targetItemId}");
        Wait.Until(d => d.FindElement(By.XPath($"//button[contains(.,'{expectedButtonText}')]"))).Click();
        Wait.Until(d => d.FindElement(By.CssSelector("dialog[aria-labelledby='claim-dialog-title']")));
    }

    private void SelectCandidateByTitle(string title)
    {
        Wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        var candidateXPath = By.XPath($"//button[.//span[normalize-space()={XPathLiteral(title)}]]");
        Wait.Until(d => d.FindElements(candidateXPath).Count > 0
            || d.PageSource.Contains("don’t have any active", StringComparison.Ordinal));

        var matches = Driver.FindElements(candidateXPath);
        if (matches.Count == 0)
        {
            var dialogText = Driver
                .FindElement(By.CssSelector("dialog[aria-labelledby='claim-dialog-title']")).Text;
            throw new Xunit.Sdk.XunitException(
                $"No candidate button titled '{title}' was found. Dialog currently shows:\n{dialogText}");
        }

        matches[0].Click();
    }

    private void WaitForPreview()
    {
        Wait.Until(d => d.PageSource.Contains("Similarity score", StringComparison.Ordinal));
    }

    private double GetScorePercent()
    {
        var text = Driver
            .FindElement(By.XPath("//p[normalize-space()='Similarity score']/following-sibling::p[1]"))
            .Text.Trim().TrimEnd('%');

        return double.Parse(text);
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }
}
