using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace AdminUserManagement.SeleniumTests;

// Selenium UI tests for the "Admin User Management" story. These drive the real React
// application end-to-end against a running frontend (http://localhost:5173) and backend
// (http://localhost:5261).
//
// NOTE ON TEST-USER USAGE: this suite uses several dev-seeded accounts, chosen to avoid any
// overlap with LoginFormTests (user1), ProfileFormTests (user2, user3), ResetPasswordFormTests
// (user4), and DeleteAccountFormTests (user15 — permanently consumed there, user16, user17):
//   - user9@example.com  : read-only search/filter target, never mutated
//   - user10@example.com : kick confirmation dialog, cancelled — never actually kicked
//   - user8@example.com  : kicked and restored (unkicked) within the same test, so it's
//                           reusable across runs
//   - user11@example.com : acts as the non-admin caller in the Scenario 5 tests
//   - user12@example.com : kicked via a non-admin's direct API call (documenting the known
//                           defect below) and restored (unkicked) within the same test
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - View All Users        -> covered
//   Scenario 2 - Search/Filter Users   -> covered
//   Scenario 3 - Successful Kick       -> covered, but see the IMPORTANT note in
//                AdminUserManagementControllerTests.cs (xUnit): the AC's wording says
//                "deleted", the implementation is a reversible kick/soft-ban. "Can no longer
//                log in" IS still verified end-to-end below.
//   Scenario 4 - Confirmation Required -> covered
//   Scenario 5 - Access Restriction (403 for non-admins) -> covered. AdminController is now
//                guarded by [Authorize(Policy = "AdminOnly")] (see AdminOnlyHandler), which
//                the two tests near the bottom of this file verify at the network level from
//                a real authenticated browser session.
public sealed class AdminUserManagementFormTests : IClassFixture<BrowserFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WebDriverWait _loginWait;

    private const string AdminEmail = "admin1@lostandfound.com";
    private const string AdminPassword = "Admin123!";

    private const string SearchTargetEmail = "user9@example.com";
    private const string SearchTargetName = "User Nine";

    private const string CancelledKickEmail = "user10@example.com";
    private const string CancelledKickName = "User Ten";

    private const string KickRestoreEmail = "user8@example.com";
    private const string KickRestoreName = "User Eight";
    private const string KickRestorePassword = "User123!";

    private const string NonAdminCallerEmail = "user11@example.com";
    private const string NonAdminCallerPassword = "User123!";

    private const string ApiKickVictimEmail = "user12@example.com";
    private const string ApiKickVictimName = "User Twelve";

    public AdminUserManagementFormTests(BrowserFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
        _loginWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(20));
    }

    // ---------- Common browser/session helpers ----------

    private void ResetBrowserSession()
    {
        _driver.Navigate().GoToUrl(BrowserFixture.BaseUrl);
        _driver.Manage().Cookies.DeleteAllCookies();
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "window.localStorage.clear(); window.sessionStorage.clear();");
    }

    private void LoginAs(string email, string password)
    {
        ResetBrowserSession();
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/login");
        _wait.Until(d => d.FindElement(By.Id("email")));

        _driver.FindElement(By.Id("email")).SendKeys(email);
        _driver.FindElement(By.Id("password")).SendKeys(password);
        _driver.FindElement(By.CssSelector("form button[type='submit']")).Click();
    }

    private void LoginAsAdmin()
    {
        LoginAs(AdminEmail, AdminPassword);
        _loginWait.Until(d => d.Url.Contains("/admin/users", StringComparison.Ordinal));
    }

    private void GoToUserManagement()
    {
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/admin/users");
        _loginWait.Until(d => d.FindElements(By.XPath("//h1[normalize-space()='User Management']")).Any());
        // Table starts in a "Loading…" state; wait for it to resolve one way or the other.
        _wait.Until(d => !d.FindElements(By.XPath("//td[contains(normalize-space(),'Loading')]")).Any());
    }

    private string BodyText() => _driver.FindElement(By.TagName("body")).Text;

    private IWebElement SearchBox() => _driver.FindElement(By.CssSelector("input[type='text']"));

    // Searches for the given email first, then waits for its row. This dev database
    // accumulates seed/throwaway accounts from every other Selenium suite over time
    // (39+ at last count), and the user list defaults to page 1 of 20, newest first
    // (see UsersRepository.SearchUsersAsync's ORDER BY created_at DESC) — so an older
    // seed account can silently fall off the unfiltered first page. Searching first
    // uses the same server-side filter SearchByEmail_FiltersListToMatchingResult
    // already relies on, which isn't affected by pagination.
    private IWebElement RowForEmail(string email)
    {
        SearchBox().Clear();
        SearchBox().SendKeys(email);
        return _wait.Until(d => d.FindElement(By.XPath($"//tr[.//td[normalize-space()='{email}']]")));
    }

    private IEnumerable<IWebElement> AllDataRows() =>
        _driver.FindElements(By.CssSelector("tbody tr"));

    private void ClickRowActionButton(IWebElement row, string label) =>
        row.FindElement(By.XPath($".//button[normalize-space()='{label}']")).Click();

    // The confirmation modal is a fixed-position overlay; its "Kick"/"Cancel" buttons are
    // scoped by the modal's own container so they're never confused with a row's own button
    // of the same name.
    private IWebElement ConfirmDialog() =>
        _wait.Until(d => d.FindElement(By.XPath("//h3[normalize-space()='Kick User' or normalize-space()='Unkick User']/ancestor::div[contains(@class,'shadow-xl')]")));

    private void ClickDialogButton(string label) =>
        ConfirmDialog().FindElement(By.XPath($".//button[normalize-space()='{label}']")).Click();

    private string RowStatusText(string email) =>
        RowForEmail(email).FindElements(By.TagName("td"))[4].Text.Trim();

    // ---------- Scenario 1: View All Users ----------

    [Fact]
    public void AdminNavigatesToUserManagement_SeesListOfRegisteredAccounts()
    {
        LoginAsAdmin();
        GoToUserManagement();

        var body = BodyText();
        Console.WriteLine("=== FULL BODY ===\n" + body + "\n=== END BODY ===");
        Assert.Contains("User Management", body);

        // Table header sanity check — proves this is the actual data table, not just page chrome.
        Assert.Contains("Email", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Status", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(AllDataRows().Count() > 0, "Expected at least one user row to be rendered.");

        // At least one known seeded regular user and one known seeded admin should be listed —
        // verified via search rather than assuming either lands on the unfiltered first page,
        // since this dev database accumulates seed/throwaway accounts from every other
        // Selenium suite and the list is newest-first, paginated at 20 (see RowForEmail).
        Assert.NotNull(RowForEmail("user1@example.com"));
        Assert.NotNull(RowForEmail(AdminEmail));
    }

    [Fact]
    public void NonAdminUser_CannotReachUserManagementPageAtAll()
    {
        // Sanity check underpinning every other test in this file: the acting admin really
        // does need to be an admin to see this page in the first place (client-side gate).
        LoginAs("user1@example.com", "User123!");
        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);

        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/admin/users");
        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);
        Assert.DoesNotContain("User Management", BodyText());
    }

    // ---------- Scenario 2: Search/Filter Users ----------

    // NOTE: UserManagementSection's search box has no debounce and doesn't cancel
    // in-flight requests — every keystroke fires its own fetchUsers() call via the
    // search-state useEffect. Typing fast can therefore, in principle, resolve out of
    // order and leave a stale intermediate result on screen. Waiting for the *expected
    // final* result (rather than just "some request finished") below sidesteps that in
    // practice, but it's worth the team adding either a debounce or a request-id/abort
    // guard so this isn't relying on favorable timing.

    [Fact]
    public void SearchByEmail_FiltersListToMatchingResult()
    {
        LoginAsAdmin();
        GoToUserManagement();

        SearchBox().SendKeys(SearchTargetEmail);
        _wait.Until(d => d.FindElements(By.XPath($"//tr[.//td[normalize-space()='{SearchTargetEmail}']]")).Any());

        var body = BodyText();
        Assert.Contains(SearchTargetEmail, body);
        // A different, non-matching seeded user must have been filtered out.
        Assert.DoesNotContain("user1@example.com", body);
    }

    [Fact]
    public void SearchByName_FiltersListToMatchingResult()
    {
        LoginAsAdmin();
        GoToUserManagement();

        SearchBox().SendKeys(SearchTargetName);
        _wait.Until(d => d.FindElements(By.XPath($"//tr[.//td[normalize-space()='{SearchTargetEmail}']]")).Any());

        Assert.Contains(SearchTargetEmail, BodyText());
    }

    [Fact]
    public void SearchWithNoMatches_ShowsEmptyState()
    {
        LoginAsAdmin();
        GoToUserManagement();

        SearchBox().SendKeys("nobody-registered-with-this-name-xyz");
        _wait.Until(d => BodyText().Contains("No users found", StringComparison.Ordinal));

        Assert.False(AllDataRows().Any(r => r.FindElements(By.TagName("td")).Count > 1));
    }

    // ---------- Scenario 4: Confirmation Required (cancel path — non-destructive) ----------

    [Fact]
    public void ClickingKick_ShowsConfirmationDialogWithWarning_CancellingLeavesUserActive()
    {
        LoginAsAdmin();
        GoToUserManagement();

        var row = RowForEmail(CancelledKickEmail);
        ClickRowActionButton(row, "Kick");

        var dialog = ConfirmDialog();
        Assert.Contains("Kick User", dialog.Text);
        Assert.Contains(CancelledKickName, dialog.Text);
        Assert.Contains("blocked from the platform", dialog.Text, StringComparison.OrdinalIgnoreCase);

        ClickDialogButton("Cancel");
        _wait.Until(d => !d.FindElements(By.XPath("//h3[normalize-space()='Kick User']")).Any());

        // Reload from scratch to prove nothing was persisted server-side, not just that the
        // dialog closed.
        _driver.Navigate().Refresh();
        GoToUserManagement();
        Assert.Equal("Active", RowStatusText(CancelledKickEmail));
    }

    // ---------- Scenario 3: Successful Kick (+ confirmation path) ----------

    [Fact]
    public void ConfirmingKick_ActuallyKicksUser_WhoCanThenNoLongerLogIn()
    {
        try
        {
            LoginAsAdmin();
            GoToUserManagement();

            var row = RowForEmail(KickRestoreEmail);
            ClickRowActionButton(row, "Kick");
            ClickDialogButton("Kick"); // the confirm button inside the dialog

            _wait.Until(d => !d.FindElements(By.XPath("//h3[normalize-space()='Kick User']")).Any());
            _wait.Until(d => RowStatusText(KickRestoreEmail) == "Kicked");

            // Prove it's actually enforced server-side, not just a client-side label swap.
            LoginAs(KickRestoreEmail, KickRestorePassword);
            var error = _wait.Until(d => d.FindElement(By.CssSelector("[role='alert']")));
            Assert.Contains("suspended", error.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(_driver.Manage().Cookies.GetCookieNamed("auth_token"));
        }
        finally
        {
            // Restore user8 to Active so this test is reusable across runs.
            LoginAsAdmin();
            GoToUserManagement();
            var row = RowForEmail(KickRestoreEmail);
            if (RowStatusText(KickRestoreEmail) == "Kicked")
            {
                ClickRowActionButton(row, "Unkick");
                ClickDialogButton("Unkick");
                _wait.Until(d => RowStatusText(KickRestoreEmail) == "Active");
            }
        }
    }

    // ---------- Scenario 5: Access Restriction (403 for non-admins) ----------
    //
    // UPDATE: AdminController is now guarded by [Authorize(Policy = "AdminOnly")] (see
    // AdminOnlyHandler, which checks user.IsAdmin), fixing the access-control gap these two
    // tests originally documented. They've been flipped to assert the correct/secure
    // behavior. LoginFormTests.RegularUser_AdminApiRequest_MustReturn403 already asserted
    // this target behavior and should now be passing too.

    [Fact]
    public void RegularUser_DirectApiCall_CannotReadTheFullAdminUserList()
    {
        LoginAs(NonAdminCallerEmail, NonAdminCallerPassword);
        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);

        var (status, body) = ExecuteAuthenticatedFetch(
            $"{BrowserFixture.AuthApiBaseUrl}/api/admin/users", method: "GET", jsonBody: null);

        Assert.Equal(403, status);
        // No user data should have leaked out alongside the rejection.
        Assert.DoesNotContain("\"users\"", body);
    }

    [Fact]
    public void RegularUser_DirectApiCall_CannotKickAnotherUser()
    {
        try
        {
            // Look up the victim's id as admin first — this uses a legitimate admin
            // session rather than assuming a non-admin could ever read this list.
            var victimId = LookUpUserIdAsAdmin(ApiKickVictimEmail);

            LoginAs(NonAdminCallerEmail, NonAdminCallerPassword);
            _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);

            var (status, _) = ExecuteAuthenticatedFetch(
                $"{BrowserFixture.AuthApiBaseUrl}/api/admin/users/{victimId}/kick",
                method: "POST",
                jsonBody: "{\"confirm\":true}");

            Assert.Equal(403, status);

            // Confirm the rejection was real, not just a client-side/status-code fiction —
            // the victim must still actually be Active.
            LoginAsAdmin();
            GoToUserManagement();
            Assert.Equal("Active", RowStatusText(ApiKickVictimEmail));
        }
        finally
        {
            // Defensive cleanup in case a prior/failed run left this account kicked.
            LoginAsAdmin();
            GoToUserManagement();
            if (RowStatusText(ApiKickVictimEmail) == "Kicked")
            {
                var row = RowForEmail(ApiKickVictimEmail);
                ClickRowActionButton(row, "Unkick");
                ClickDialogButton("Unkick");
                _wait.Until(d => RowStatusText(ApiKickVictimEmail) == "Active");
            }
        }
    }

    // ---------- Setup helpers ----------

    private string LookUpUserIdAsAdmin(string email)
    {
        LoginAsAdmin();
        var (status, body) = ExecuteAuthenticatedFetch(
            $"{BrowserFixture.AuthApiBaseUrl}/api/admin/users?search={Uri.EscapeDataString(email)}",
            method: "GET",
            jsonBody: null);
        Assert.Equal(200, status);

        using var doc = JsonDocument.Parse(body);
        var match = doc.RootElement.GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == email);
        return match.GetProperty("id").GetString()!;
    }

    // Mirrors LoginFormTests.ExecuteAuthenticatedFetchStatus, extended to support a request
    // body and to return the response text as well as the status code.
    private (int Status, string Body) ExecuteAuthenticatedFetch(string url, string method, string? jsonBody)
    {
        var script = """
            const url = arguments[0];
            const method = arguments[1];
            const body = arguments[2];
            const done = arguments[arguments.length - 1];
            const init = { method, credentials: 'include' };
            if (body !== null) {
              init.headers = { 'Content-Type': 'application/json' };
              init.body = body;
            }
            fetch(url, init)
              .then(r => r.text().then(text => done([r.status, text])))
              .catch(() => done([-1, '']));
            """;

        var result = ((IJavaScriptExecutor)_driver).ExecuteAsyncScript(script, url, method, jsonBody);
        var list = (System.Collections.ObjectModel.ReadOnlyCollection<object>)result!;
        return (Convert.ToInt32(list[0]), (string)list[1]);
    }
}
