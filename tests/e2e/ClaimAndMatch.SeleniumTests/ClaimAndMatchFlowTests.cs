using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ClaimAndMatch.SeleniumTests;

// Story 2: the report picker popup, preview/comparison, submitting a claim, and Matched Items.
// User B only ever creates FOUND reports, User A only ever creates LOST reports for cross-user pairs,
// keeping "User B has zero lost reports" true for the Scenario 1 empty-state test.
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

    // Scenario 1: empty candidates show the empty-state message and a create-report link.
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

    // Scenarios 1 + 2: the picker lists my own candidates, selecting one previews both reports with
    // a score. Also covers Scenario 3's no-image branch.
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

    // Scenario 3, wait half: one side's image analysis is still pending, so the preview shows a
    // wait message instead of a score.
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

    // Scenario 3, completed half: both sides have a completed image description, so the preview
    // succeeds with a score.
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

    // Scenario 4: an eligible score enables Confirm and claim, and the match then shows up in
    // Matched Items.
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

        Assert.Contains("Awaiting finder", Driver.PageSource, StringComparison.Ordinal);
    }

    // Scenario 5: a low score disables Confirm and claim; cancel and reopen runs a fresh comparison.
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

        OpenClaimDialog(foundId, "This Is My Lost Item");
        SelectCandidateByTitle(lostTitle);
        WaitForPreview();
        Assert.False(
            Driver.FindElement(By.XPath("//button[contains(.,'Confirm and claim')]")).Enabled);
    }

    // Scenario 6, both-mine half: a user cannot claim between two reports they own themselves. The
    // other cases are unreachable through this picker or concurrency-shaped - stay xUnit/JMeter's job.
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
