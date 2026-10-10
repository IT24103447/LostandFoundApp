using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace AdminSpamReview.SeleniumTests;

// Story LF-87: an admin reviews the detected-spam queue - the status tabs, the per-row contact /
// score / status rendering, sort and the from/to + user-search filters, paging on scroll, and the
// deleted-user and filtered-empty states the review screen has to survive.
//
// The query/filter logic itself is already covered end-to-end at the API level in
// SpamReviewApiTests (the whole /api/admin/spam-records surface through the real host), so this
// file exists only for the browser parts: the page actually renders the rows the API returns, the
// controls drive the API with the right parameters, and the UI states (empty, deleted user,
// invalid range notice, load-more) appear for the admin.
public sealed class SpamReviewFlowTests : IClassFixture<AdminSpamReviewFixture>
{
    private readonly AdminSpamReviewFixture _fixture;
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public SpamReviewFlowTests(AdminSpamReviewFixture fixture)
    {
        _fixture = fixture;
        _driver = fixture.Driver;
        _wait = fixture.Wait;
    }

    [Fact]
    public void AdminLogin_OpensSpamReview_WithEmptyActiveQueue()
    {
        _fixture.PurgeSeleniumSpamRows();

        GoToSpamReview();

        var empty = _wait.Until(d => d.FindElement(By.Id("spam-review-empty")));
        Assert.Equal("No active Spam records.", empty.Text);
    }

    [Fact]
    public void SpamReviewTabs_ShowOnlyTheRecordsForThatStatus()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR tabs {NewTag()} User");
        var needReview = _fixture.SeedSpamRecord(user, 3, "NEEDS_REVIEW", Utc(2026, 10, 1, 9));
        var underReview = _fixture.SeedSpamRecord(user, 5, "UNDER_REVIEW", Utc(2026, 10, 2, 9));
        var pendingSolve = _fixture.SeedSpamRecord(user, 7, "PENDING_SOLVE", Utc(2026, 10, 3, 9));
        var dismissed = _fixture.SeedSpamRecord(user, 2, "DISMISSED", Utc(2026, 10, 4, 9));
        var solved = _fixture.SeedSpamRecord(user, 9, "SOLVED", Utc(2026, 10, 5, 9));

        try
        {
            GoToSpamReview();

            // Active is the default tab and covers the three reviewable statuses. The assertion
            // compares against the same alphabetized order the rows are read in.
            WaitForRowCount(3);
            Assert.Equal(
                new[] { "NEEDS_REVIEW", "PENDING_SOLVE", "UNDER_REVIEW" },
                Rows().Select(r => r.GetAttribute("data-status")).OrderBy(s => s));

            ClickTab("dismissed");
            WaitForRowCount(1);
            Assert.Equal(new[] { "DISMISSED" }, Rows().Select(r => r.GetAttribute("data-status")));

            ClickTab("solved");
            WaitForRowCount(1);
            Assert.Equal(new[] { "SOLVED" }, Rows().Select(r => r.GetAttribute("data-status")));
        }
        finally
        {
            _fixture.DeleteSeeded(
                [needReview, underReview, pendingSolve, dismissed, solved],
                [user]);
        }
    }

    [Fact]
    public void SpamReviewRow_ShowsContactScoreStatusAndDisabledAction()
    {
        _fixture.PurgeSeleniumSpamRows();
        var name = $"Selenium SR row {NewTag()} Valentine";
        var user = _fixture.CreateThrowawayUser(name);
        var recordId = _fixture.SeedSpamRecord(user, 7, "NEEDS_REVIEW", Utc(2026, 10, 6, 9));

        try
        {
            GoToSpamReview();
            WaitForRowCount(1);

            // The contact cell fetches /api/admin/users/{id} after the list lands - wait for the
            // name, not just for the row.
            var row = _wait.Until(d =>
                Rows().Count == 1 && Rows()[0]
                    .FindElement(By.XPath(".//td[1]")).Text
                    .Contains(name, StringComparison.Ordinal)
                    ? Rows()[0]
                    : null);

            var rendered = row!;
            Assert.Equal("NEEDS_REVIEW", rendered.GetAttribute("data-status"));
            Assert.Equal(7, ScoreOf(rendered));
            Assert.Contains(
                "Needs review",
                rendered.FindElement(By.XPath(".//td[3]")).Text,
                StringComparison.Ordinal);

            var view = rendered.FindElement(By.Id($"spam-record-view-{recordId}"));
            Assert.Equal("View record", view.Text);
            Assert.False(view.Enabled, "Expected View record to be disabled while opening a record is not yet wired up.");
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
        }
    }

    [Fact]
    public void SpamReviewSort_ReordersByScoreThenByDate()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR sort {NewTag()} User");
        var low = _fixture.SeedSpamRecord(user, 2, "NEEDS_REVIEW", Utc(2026, 10, 1, 9));
        var high = _fixture.SeedSpamRecord(user, 9, "NEEDS_REVIEW", Utc(2026, 10, 2, 9));
        var mid = _fixture.SeedSpamRecord(user, 5, "NEEDS_REVIEW", Utc(2026, 10, 3, 9));

        try
        {
            GoToSpamReview();

            // Score (highest first) is the default sort.
            _wait.Until(d => Rows().Count == 3 && ScoreOf(Rows()[0]) == 9);

            SelectSortBy("date");
            _wait.Until(d => Rows().Count == 3 && ScoreOf(Rows()[0]) == 5);
        }
        finally
        {
            _fixture.DeleteSeeded([low, high, mid], [user]);
        }
    }

    [Fact]
    public void SpamReviewDateFilter_NarrowsByFlaggedDate()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR dates {NewTag()} User");
        var outside = _fixture.SeedSpamRecord(user, 4, "NEEDS_REVIEW", Utc(2026, 10, 1, 9));
        var inside1 = _fixture.SeedSpamRecord(user, 6, "NEEDS_REVIEW", Utc(2026, 10, 15, 9));
        var inside2 = _fixture.SeedSpamRecord(user, 8, "NEEDS_REVIEW", Utc(2026, 10, 20, 9));

        try
        {
            GoToSpamReview();
            WaitForRowCount(3);

            SetDateInput("spam-review-from", "2026-10-10");
            SetDateInput("spam-review-to", "2026-10-25");
            ClickApply();

            WaitForRowCount(2);
            Assert.NotNull(RowOrNull(inside1));
            Assert.NotNull(RowOrNull(inside2));
            Assert.Null(RowOrNull(outside));

            ClickClear();
            WaitForRowCount(3);
            Assert.NotNull(RowOrNull(outside));
        }
        finally
        {
            _fixture.DeleteSeeded([outside, inside1, inside2], [user]);
        }
    }

    [Fact]
    public void SpamReviewDateFilter_InvalidRange_ShowsNoticeAndKeepsTheList()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR notice {NewTag()} User");
        var recordId = _fixture.SeedSpamRecord(user, 5, "NEEDS_REVIEW", Utc(2026, 10, 6, 9));

        try
        {
            GoToSpamReview();
            WaitForRowCount(1);

            SetDateInput("spam-review-from", "2026-10-20");
            SetDateInput("spam-review-to", "2026-10-10");
            ClickApply();

            var notice = _wait.Until(d => d.FindElement(By.Id("spam-review-notice")));
            Assert.Equal("The from date must not be after the to date.", notice.Text);

            // The invalid range is refused client-side: no new request, list untouched.
            var row = Assert.Single(Rows());
            Assert.NotNull(row);
            Assert.NotNull(RowOrNull(recordId));
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
        }
    }

    [Fact]
    public void SpamReviewSearch_ByFlaggedUserName_FiltersToThatUser()
    {
        _fixture.PurgeSeleniumSpamRows();
        var tag = NewTag();
        var baileyName = $"Selenium SR {tag} Bailey";
        var carterName = $"Selenium SR {tag} Carter";
        var bailey = _fixture.CreateThrowawayUser(baileyName);
        var carter = _fixture.CreateThrowawayUser(carterName);
        var bailey1 = _fixture.SeedSpamRecord(bailey, 5, "NEEDS_REVIEW", Utc(2026, 10, 1, 9));
        var bailey2 = _fixture.SeedSpamRecord(bailey, 3, "NEEDS_REVIEW", Utc(2026, 10, 2, 9));
        var carter1 = _fixture.SeedSpamRecord(carter, 9, "NEEDS_REVIEW", Utc(2026, 10, 3, 9));

        try
        {
            GoToSpamReview();
            WaitForRowCount(3);

            _driver.FindElement(By.Id("spam-review-search")).SendKeys("Bailey");
            ClickApply();

            _wait.Until(d =>
                Rows().Count == 2 &&
                Rows().All(row =>
                    row.FindElement(By.XPath(".//td[1]")).Text
                        .Contains(baileyName, StringComparison.Ordinal)));

            Assert.Null(RowOrNull(carter1));
            Assert.Equal("Bailey", _driver.FindElement(By.Id("spam-review-search")).GetAttribute("value"));

            ClickClear();
            WaitForRowCount(3);
            Assert.NotNull(RowOrNull(carter1));
        }
        finally
        {
            _fixture.DeleteSeeded([bailey1, bailey2, carter1], [bailey, carter]);
        }
    }

    [Fact]
    public void SpamReview_LoadMore_AppendsTheSecondPageOnScroll()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR pages {NewTag()} User");
        var recordIds = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            recordIds.Add(
                _fixture.SeedSpamRecord(user, 1 + i, "NEEDS_REVIEW", Utc(2026, 9, 30, 9).AddDays(i)));
        }

        try
        {
            GoToSpamReview();

            // First page renders 20 (the API's page size), with the sentinel awaiting the scroll.
            _wait.Until(d => Rows().Count == 20);

            ((IJavaScriptExecutor)_driver).ExecuteScript(
                "arguments[0].scrollIntoView();",
                _driver.FindElement(By.Id("spam-review-more")));

            _wait.Until(d => Rows().Count == 25);
        }
        finally
        {
            _fixture.DeleteSeeded([.. recordIds], [user]);
        }
    }

    [Fact]
    public void DeletedUser_ContactCellShowsDeletedUser()
    {
        _fixture.PurgeSeleniumSpamRows();
        var name = $"Selenium SR gone {NewTag()} User";
        var user = _fixture.CreateThrowawayUser(name);
        var recordId = _fixture.SeedSpamRecord(user, 6, "NEEDS_REVIEW", Utc(2026, 10, 7, 9));
        _fixture.SoftDeleteUser(user);

        try
        {
            GoToSpamReview();

            var row = _wait.Until(d => RowOrNull(recordId));
            _wait.Until(d =>
                row is not null &&
                row.FindElement(By.XPath(".//td[1]")).Text
                    .Contains("Deleted user", StringComparison.Ordinal));

            var cellText = row!
                .FindElement(By.XPath(".//td[1]"))
                .Text;
            Assert.Contains("Deleted user", cellText, StringComparison.Ordinal);
            Assert.DoesNotContain(name, cellText, StringComparison.Ordinal);
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
        }
    }

    [Fact]
    public void SpamReviewSearch_NoMatchingUser_ShowsFilteredEmptyState()
    {
        _fixture.PurgeSeleniumSpamRows();
        var user = _fixture.CreateThrowawayUser($"Selenium SR none {NewTag()} User");
        var recordId = _fixture.SeedSpamRecord(user, 4, "NEEDS_REVIEW", Utc(2026, 10, 8, 9));

        try
        {
            GoToSpamReview();
            WaitForRowCount(1);

            _driver.FindElement(By.Id("spam-review-search")).SendKeys("NoSuchUserForSeleniumXYZ");
            ClickApply();

            var empty = _wait.Until(d => d.FindElement(By.Id("spam-review-empty")));
            Assert.Equal("No Spam records match these filters.", empty.Text);

            ClickClear();
            WaitForRowCount(1);
            Assert.NotNull(RowOrNull(recordId));
        }
        finally
        {
            _fixture.DeleteSeeded([recordId], [user]);
        }
    }

    // ---- Helpers -----------------------------------------------------------

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
    }

    private IReadOnlyList<IWebElement> Rows() =>
        _driver.FindElements(By.CssSelector("#spam-review-table tbody tr"));

    private void WaitForRowCount(int expected)
    {
        _wait.Until(d => Rows().Count == expected);
    }

    private IWebElement? RowOrNull(Guid id) =>
        _driver.FindElements(By.Id($"spam-record-{id}")).FirstOrDefault();

    private static int ScoreOf(IWebElement row) =>
        int.Parse(row.FindElement(By.XPath(".//td[2]/span")).Text);

    private void ClickTab(string value)
    {
        _driver.FindElement(By.Id($"spam-review-tab-{value}")).Click();
        WaitForListToSettle();
    }

    private void ClickApply() => _driver.FindElement(By.Id("spam-review-apply")).Click();
    private void ClickClear() => _driver.FindElement(By.Id("spam-review-clear")).Click();

    // Same controlled-input caveat as SetDateInput: set the select through the native setter so
    // React's onChange hears about it (SelectElement lives in the separate Selenium.Support
    // package the sibling Selenium projects deliberately do not reference).
    private void SelectSortBy(string value)
    {
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = document.getElementById('spam-review-sort');" +
            "const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set;" +
            "setter.call(el, arguments[0]);" +
            "el.dispatchEvent(new Event('change', { bubbles: true }));",
            value);
    }

    // React tracks <input type="date"> by its value via an internal tracker; setting el.value
    // directly can bypass the tracker so onChange never fires. Driving it through the native
    // prototype setter - the technique React itself documents - makes the change register
    // deterministically, then we dispatch the input event the browser would.
    private void SetDateInput(string id, string value)
    {
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = document.getElementById(arguments[0]);" +
            "const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;" +
            "setter.call(el, arguments[1]);" +
            "el.dispatchEvent(new Event('input', { bubbles: true }));",
            id,
            value);
    }
}