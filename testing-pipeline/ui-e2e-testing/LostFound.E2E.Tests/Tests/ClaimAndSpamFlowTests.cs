using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Cross-service journeys that reach the Matching and Admin Verify services (test plan FLOW-03, FLOW-06).
/// Both depend on parts of the pipeline environment that are switched off or asynchronous (Gemini image
/// analysis, spam detection over Kafka), so they assert what is deterministic and record the rest.
/// FLOW-04 (confirmed match visible to both users) is out of scope: it needs an eligible similarity score,
/// which cannot be produced while Gemini is off.
/// </summary>
public class ClaimAndSpamFlowTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    private readonly ITestOutputHelper _output;

    public ClaimAndSpamFlowTests(BrowserFixture fixture, ITestOutputHelper output) : base(fixture) => _output = output;

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "User B reports a found item and user A a lost one (both with a photo). A opens the found item and starts a claim: the picker lists A's own lost report only, and selecting it reaches a defined preview state (a similarity score, or a photo-not-ready message while image analysis and photo matching are unavailable).")]
    public void FLOW_03_ClaimDialog_ListsOwnCandidates_AndReachesAPreviewState() => Run(nameof(FLOW_03_ClaimDialog_ListsOwnCandidates_AndReachesAPreviewState), () =>
    {
        var userA = TestConfig.PickUser();                 // owns the LOST report and makes the claim
        var userB = TestConfig.PickUserOtherThan(userA);   // owns the FOUND report

        // ARRANGE through the APIs. Photos are attached so the Matching service has to wait for image
        // analysis; with Gemini off that wait never ends, which makes the pending state reproducible.
        var (lostTitle, _) = ItemApi.CreateLostItemWithId(userA, ItemApi.UniqueTitle("Navy umbrella"), withPhoto: true);
        var (foundTitle, foundId) = ItemApi.CreateFoundItemWithId(userB, ItemApi.UniqueTitle("Navy umbrella"), withPhoto: true);

        // ACT: user A opens the found item and starts a claim.
        new LoginPage(Driver).LoginAs(userA);
        new HomePage(Driver).WaitUntilLoaded();

        var dialog = new ClaimDialogPage(Driver)
            .OpenUntilCandidateListed(foundId, foundTitle, lostTitle);   // Auth + Item + Matching, via Kafka

        // The picker offers the caller's own reports of the opposite type, never someone else's report.
        var candidates = dialog.CandidateTitles();
        Assert.Contains(lostTitle, candidates);
        Assert.DoesNotContain(foundTitle, candidates);

        // Selecting a candidate must end in a defined state, not hang on "Comparing the reports…".
        var result = dialog.SelectCandidate(lostTitle).WaitForPreviewOutcome();
        _output.WriteLine($"FLOW-03 preview outcome: {result.Outcome}. {Shorten(result.Detail)}");

        // Defined outcomes: a score, or the Matching service saying the photos cannot be compared yet
        // (still being analysed / not ready). Anything else (other error, hang) is a failure.
        Assert.True(
            result.Outcome is PreviewOutcome.Score or PreviewOutcome.StillAnalysing or PreviewOutcome.PhotoNotReady,
            $"Expected a score or a photo-not-ready message but got {result.Outcome}: {Shorten(result.Detail)}");

        if (result.Outcome == PreviewOutcome.Score)
            Assert.InRange(dialog.ScorePercent(), 0, 100);
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "A reserved user posts four reports in a row; the admin's Spam Review queue then shows more listings flagged for that user than before. Skipped when E2E_SPAM_DETECTION=false.")]
    public void FLOW_06_BurstOfReports_AppearsInAdminSpamReview() => Run(nameof(FLOW_06_BurstOfReports_AppearsInAdminSpamReview), () =>
    {
        if (!TestConfig.SpamDetectionExpected)
        {
            _output.WriteLine("FLOW-06 skipped: E2E_SPAM_DETECTION=false (no spam detection in this environment).");
            return;
        }

        var spammer = TestConfig.SpamTarget;   // reserved account, used by no other test

        // Admin reads the current state first, so a case left by an earlier run cannot satisfy the test.
        new LoginPage(Driver).LoginAs(TestConfig.Admin);
        new AdminHomePage(Driver).WaitUntilLoaded();
        var spamReview = new SpamReviewPage(Driver).Open();
        var before = spamReview.TotalScoreFor(spammer.Email);

        // ARRANGE: the burst. Item service publishes each report to Kafka; Admin Verify counts them.
        for (var i = 1; i <= 4; i++)
            ItemApi.CreateLostItem(spammer, ItemApi.UniqueTitle($"Spam burst {i}"));

        // ASSERT: poll the observable outcome (no sleeping) until the queue reflects the burst.
        var after = spamReview.WaitForScoreAbove(spammer.Email, before);
        _output.WriteLine($"FLOW-06 listings flagged for {spammer.Email}: before={before}, after={after?.ToString() ?? "unchanged"}.");

        Assert.True(
            after is not null,
            $"No new spam case appeared for {spammer.Email} within the timeout. " +
            "If spam detection is not running in this environment, set E2E_SPAM_DETECTION=false.");
    });

    private static string Shorten(string text)
    {
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
