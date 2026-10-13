using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace AdminSpamReview.SeleniumTests;

// Story LF-267 / LF-268 / LF-269: the admin's case actions on one spam record, driven in the
// browser through the Spam record popup - open (warn-first when the record needs review, straight
// in when it is already under review, read-only when it is closed), dismiss with its tinted
// confirmation, and solve (the browser runner: deletes each remaining listing through the
// ItemService admin API, kicks on request, then finishes the record; resume from a PENDING_SOLVE
// record). The API logic itself is already covered end-to-end in SpamReviewApiTests /
// SpamReviewCaseApiTests; this file is the browser part - the popup actually opens, the
// confirmations actually drive the endpoints with the right options, and the record lands in the
// state the UI promises.
//
// Every listing an item is seeded for is QA throwaway data (tracked_listings, the link and the
// ItemService row) so the Solve runner's admin deletes can never touch a real listing. Rows are
// matched by id, never by count, because the live dev DB carries other seeded rows too.
//
// One behavior is intentionally left as a scripted manual check rather than an automated
// assertion: the leave-site "you have a Solve in progress" browser prompt. That native prompt is
// a beforeunload dialog the browser owns, not page content Selenium can assert, so it is checked
// by hand and recorded in the execution log instead.
public sealed class SpamRecordCaseFlowTests : IClassFixture<AdminSpamReviewFixture>
{
    private readonly AdminSpamReviewFixture _fixture;
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public SpamRecordCaseFlowTests(AdminSpamReviewFixture fixture)
    {
        _fixture = fixture;
        _driver = fixture.Driver;
        _wait = fixture.Wait;
    }

    // ---- LF-267: open -------------------------------------------------------------------------

    [Fact]
    public void LF267_Open_NeedsReview_WarningExplains_ConfirmMovesToUnderReview()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF267 open {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 8, "NEEDS_REVIEW", now.AddSeconds(-5));
        var listingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-10));
        _fixture.SeedLinkedList(recordId, listingId, now.AddSeconds(-8));
        _fixture.SeedLostItem(listingId, user, "Grey laptop sleeve");

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();

            // Needs review opens with the warning first, not the record itself.
            var warning = _wait.Until(d => d.FindElement(By.Id("spam-open-warning")));
            Assert.Contains("Open Spam record", warning.Text, StringComparison.Ordinal);
            Assert.Contains("can't be undone", warning.Text, StringComparison.Ordinal);

            // Cancel keeps the record untouched.
            warning.FindElement(By.Id("spam-open-cancel")).Click();
            Assert.Empty(_driver.FindElements(By.Id("spam-record-dialog")));
            Assert.Equal("NEEDS_REVIEW", _fixture.ReadSpamStatus(recordId));

            // Confirming opens it and lands the record under review.
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();
            _wait.Until(d => d.FindElement(By.Id("spam-open-warning")))
                .FindElement(By.Id("spam-open-confirm"))
                .Click();

            WaitForStatus("Under review");
            Assert.Equal("8", _driver.FindElement(By.Id("spam-record-score")).Text);

            // The linked listing renders with its real title, an Active badge and its photo strip.
            _wait.Until(d => d.FindElement(By.Id($"spam-listing-{listingId}")).Text
                .Contains("Grey laptop sleeve", StringComparison.Ordinal));
            Assert.Equal("Active", _wait.Until(d => d.FindElement(By.Id($"spam-listing-state-{listingId}")).Text));
            _wait.Until(d => d.FindElements(By.Id($"spam-listing-photos-{listingId}")).Count == 1);

            Assert.Equal("UNDER_REVIEW", _fixture.ReadSpamStatus(recordId));

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(listingId);
        }
    }

    [Fact]
    public void LF267_Open_AlreadyUnderReview_NoWarning_OpensDirectly()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF267 open {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 5, "UNDER_REVIEW", now.AddSeconds(-5));
        // No ItemService row on purpose: an item-less listing shows the fallback title and a
        // "State unavailable" badge, and the record still opens.
        var listingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-10));
        _fixture.SeedLinkedList(recordId, listingId, now.AddSeconds(-8));

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();

            _wait.Until(d => d.FindElement(By.Id("spam-record-dialog")));
            Assert.Empty(_driver.FindElements(By.Id("spam-open-warning")));

            WaitForStatus("Under review");
            _wait.Until(d => d.FindElement(By.Id($"spam-listing-state-{listingId}")).Text == "State unavailable");
            Assert.Contains("Lost listing", _driver.FindElement(By.Id($"spam-listing-{listingId}")).Text, StringComparison.Ordinal);

            // Both actions are available for an under-review record.
            _wait.Until(d => d.FindElement(By.Id("spam-record-dismiss")));
            _wait.Until(d => d.FindElement(By.Id("spam-record-solve")));

            Assert.Equal("UNDER_REVIEW", _fixture.ReadSpamStatus(recordId));

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(listingId);
        }
    }

    [Fact]
    public void LF267_Open_ClosedRecord_ShowsReadOnlyWithoutActions()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF267 closed {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 3, "SOLVED", now.AddSeconds(-2));

        try
        {
            GoToSpamReview();
            ClickTab("solved");
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();

            _wait.Until(d => d.FindElement(By.Id("spam-record-dialog")));
            WaitForReadOnly("This record is closed.");

            Assert.Empty(_driver.FindElements(By.Id("spam-record-dismiss")));
            Assert.Empty(_driver.FindElements(By.Id("spam-record-solve")));

            Assert.Equal("SOLVED", _fixture.ReadSpamStatus(recordId));

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
        }
    }

    // ---- LF-268: dismiss ----------------------------------------------------------------------

    [Fact]
    public void LF268_Dismiss_FromUnderReview_CancelKeeps_ConfirmDismisses()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF268 dismiss {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 6, "UNDER_REVIEW", now.AddSeconds(-5));
        var listingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-10));
        _fixture.SeedLinkedList(recordId, listingId, now.AddSeconds(-8));

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();
            WaitForStatus("Under review");

            // Dismiss asks for the tinted confirmation, and cancelling leaves the record alone.
            _driver.FindElement(By.Id("spam-record-dismiss")).Click();
            var confirm = _wait.Until(d => d.FindElement(By.Id("spam-dismiss-confirm-dialog")));
            Assert.Contains("false positive", confirm.Text, StringComparison.Ordinal);
            confirm.FindElement(By.Id("spam-dismiss-cancel")).Click();
            Assert.Empty(_driver.FindElements(By.Id("spam-dismiss-confirm-dialog")));
            Assert.Equal("UNDER_REVIEW", _fixture.ReadSpamStatus(recordId));

            // Confirming dismisses: the popup closes and the record leaves the active queue.
            _driver.FindElement(By.Id("spam-record-dismiss")).Click();
            _wait.Until(d => d.FindElement(By.Id("spam-dismiss-confirm-dialog")))
                .FindElement(By.Id("spam-dismiss-confirm"))
                .Click();

            _wait.Until(d => RowOrNull(recordId) == null);
            Assert.Equal("DISMISSED", _fixture.ReadSpamStatus(recordId));

            // And it surfaces on the Dismissed tab, still in the state the UI promised.
            ClickTab("dismissed");
            var dismissedRow = WaitForRow(recordId);
            Assert.Equal("DISMISSED", dismissedRow.GetAttribute("data-status"));
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(listingId);
        }
    }

    // ---- LF-269: solve ------------------------------------------------------------------------

    [Fact]
    public void LF269_Solve_ActiveItem_DeletesListing_FinishSolves()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF269 solve {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 9, "UNDER_REVIEW", now.AddSeconds(-5));
        var listingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-10));
        _fixture.SeedLinkedList(recordId, listingId, now.AddSeconds(-8));
        _fixture.SeedLostItem(listingId, user, "Blue umbrella");

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();
            WaitForStatus("Under review");
            _wait.Until(d => d.FindElement(By.Id($"spam-listing-state-{listingId}")).Text == "Active");

            // The solve confirmation previews exactly what will happen: one delete, nothing skipped.
            _driver.FindElement(By.Id("spam-record-solve")).Click();
            var confirm = _wait.Until(d => d.FindElement(By.Id("spam-solve-confirm-dialog")));
            Assert.Contains("Will delete (1)", confirm.Text, StringComparison.Ordinal);
            Assert.Contains("Blue umbrella", confirm.Text, StringComparison.Ordinal);
            Assert.Empty(_driver.FindElements(By.Id("spam-solve-will-skip")));

            // Confirm and let the runner delete the listing, then finish the record.
            confirm.FindElement(By.Id("spam-solve-confirm")).Click();
            _wait.Until(d => d.FindElement(By.Id("spam-solve-done")).Text
                .Contains("Solve finished. This record is now Solved.", StringComparison.Ordinal));

            _wait.Until(d => d.FindElement(By.Id($"spam-listing-result-{listingId}")).Text == "Deleted by Solve");
            Assert.Empty(_driver.FindElements(By.Id("spam-record-kick")));

            // End state: record solved, the listing marked deleted, and the ItemService row
            // actually soft-deleted through the admin delete endpoint.
            Assert.Equal("SOLVED", _fixture.ReadSpamStatus(recordId));
            Assert.Equal("DELETED", _fixture.ReadListingSolveResult(recordId, listingId));
            Assert.True(_fixture.ReadItemDeletedAt(listingId) is true, "The ItemService row is soft-deleted after Solve.");

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(listingId);
        }
    }

    [Fact]
    public void LF269_Solve_WithKick_KicksUser_FinishSolves()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF269 kick {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 7, "UNDER_REVIEW", now.AddSeconds(-5));
        var listingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-10));
        _fixture.SeedLinkedList(recordId, listingId, now.AddSeconds(-8));
        _fixture.SeedLostItem(listingId, user, "Wired headset");

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();
            WaitForStatus("Under review");
            _wait.Until(d => d.FindElement(By.Id($"spam-listing-state-{listingId}")).Text == "Active");

            // Ask for the kick, then confirm - the runner kicks the user after the listings.
            _driver.FindElement(By.Id("spam-record-solve")).Click();
            var confirm = _wait.Until(d => d.FindElement(By.Id("spam-solve-confirm-dialog")));
            confirm.FindElement(By.Id("spam-solve-kick")).Click();
            confirm.FindElement(By.Id("spam-solve-confirm")).Click();

            _wait.Until(d => d.FindElement(By.Id("spam-solve-done")).Text
                .Contains("Solve finished. This record is now Solved.", StringComparison.Ordinal));
            _wait.Until(d => d.FindElement(By.Id("spam-record-kick")).Text == "Kick: user kicked");

            // End state: solved, and Auth Service marks the user kicked + the record remembers it.
            Assert.Equal("SOLVED", _fixture.ReadSpamStatus(recordId));
            Assert.Equal("KICKED", _fixture.ReadKickStatus(recordId));
            Assert.True(_fixture.ReadUserKicked(user), "The kicked user is flagged in Auth Service.");
            Assert.Equal("DELETED", _fixture.ReadListingSolveResult(recordId, listingId));

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(listingId);
        }
    }

    [Fact]
    public void LF269_Solve_ResumeFromPendingSolve_FinishesRemainingListing()
    {
        _fixture.PurgeSeleniumCaseData();
        var user = _fixture.CreateThrowawayUser($"Selenium LF269 resume {NewTag()} User");
        var now = DateTime.UtcNow;
        var recordId = _fixture.SeedSpamRecord(user, 4, "PENDING_SOLVE", now.AddSeconds(-5));
        // One listing already skipped by an earlier solve session, one still to handle.
        var doneId = _fixture.SeedTrackedListing(user, now.AddSeconds(-15), "LOST");
        var pendingId = _fixture.SeedTrackedListing(user, now.AddSeconds(-20), "LOST");
        _fixture.SeedLinkedList(recordId, doneId, now.AddSeconds(-10), "SKIPPED");
        _fixture.SeedLinkedList(recordId, pendingId, now.AddSeconds(-10), null);
        _fixture.SeedLostItem(pendingId, user, "Rubber duck");

        try
        {
            GoToSpamReview();
            WaitForRow(recordId);
            RowOrNull(recordId)!.FindElement(By.Id($"spam-record-view-{recordId}")).Click();
            WaitForStatus("Pending solve");

            // A pending-solve record offers Resume Solve, with the saved kick note and no checkbox.
            _wait.Until(d => d.FindElement(By.Id("spam-record-solve")).Text == "Resume Solve");
            _driver.FindElement(By.Id("spam-record-solve")).Click();
            var confirm = _wait.Until(d => d.FindElement(By.Id("spam-solve-confirm-dialog")));
            Assert.Contains("Resume Solve for this record?", confirm.Text, StringComparison.Ordinal);
            _wait.Until(d => d.FindElement(By.Id("spam-solve-kick-saved")).Text == "The user won't be kicked.");
            Assert.Empty(_driver.FindElements(By.Id("spam-solve-kick")));

            confirm.FindElement(By.Id("spam-solve-confirm")).Click();
            _wait.Until(d => d.FindElement(By.Id("spam-solve-done")).Text
                .Contains("Solve finished. This record is now Solved.", StringComparison.Ordinal));

            // The already-skipped listing stays skipped; the remaining one got deleted.
            Assert.Equal("SOLVED", _fixture.ReadSpamStatus(recordId));
            Assert.Equal("SKIPPED", _fixture.ReadListingSolveResult(recordId, doneId));
            Assert.Equal("DELETED", _fixture.ReadListingSolveResult(recordId, pendingId));
            _wait.Until(d => d.FindElement(By.Id($"spam-listing-result-{pendingId}")).Text == "Deleted by Solve");

            CloseDialog();
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
            _fixture.DeleteSeededItems(doneId, pendingId);
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    private static DateTime Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private void GoToSpamReview()
    {
        _fixture.EnsureAdminSession();
        _fixture.NavigateToSpamReview();
        WaitForListToSettle();
    }

    private void WaitForListToSettle()
    {
        _wait.Until(d => !d.PageSource.Contains("Loading Spam records", StringComparison.Ordinal));
        _wait.Until(d => d.FindElements(By.Id("spam-review-table")).Count > 0);
    }

    private IWebElement? RowOrNull(Guid id) =>
        _driver.FindElements(By.Id($"spam-record-{id}")).FirstOrDefault();

    private IWebElement WaitForRow(Guid id)
    {
        var idStr = id.ToString();
        var end = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < end)
        {
            try
            {
                var d = _driver;
                // If empty state is shown, wait (list may reload)
                if (d.FindElements(By.Id("spam-review-empty")).Count > 0)
                {
                    System.Threading.Thread.Sleep(200);
                    continue;
                }
                // Try direct find by id attribute (row is spam-record-{id})
                var target = d.FindElements(By.Id($"spam-record-{idStr}"));
                if (target.Count > 0)
                    return target[0];
                // Also search by CSS selector scoped to table
                target = d.FindElements(By.CssSelector($"#spam-review-table #spam-record-{idStr}"));
                if (target.Count > 0)
                    return target[0];
                // Look for load more and click if visible
                var loadMore = d.FindElements(By.Id("spam-review-load-more"));
                if (loadMore.Count > 0 && loadMore[0].Displayed && loadMore[0].Enabled)
                {
                    try { loadMore[0].Click(); System.Threading.Thread.Sleep(400); } catch { }
                }
                else
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
            catch
            {
                System.Threading.Thread.Sleep(200);
            }
        }
        throw new OpenQA.Selenium.WebDriverTimeoutException($"Row {idStr} not found");
    }

    private void ClickTab(string value)
    {
        _driver.FindElement(By.Id($"spam-review-tab-{value}")).Click();
        WaitForListToSettle();
    }

    private void WaitForStatus(string label) =>
        _wait.Until(d => d.FindElement(By.Id("spam-record-status")).Text == label);

    private void WaitForReadOnly(string note) =>
        _wait.Until(d => d.FindElement(By.Id("spam-record-readonly")).Text == note);

    private void CloseDialog()
    {
        _driver.FindElement(By.Id("spam-record-close")).Click();
        _wait.Until(d => d.FindElements(By.Id("spam-record-dialog")).Count == 0);
    }
}