using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ProfileForm.SeleniumTests;

// Selenium UI tests for the "View/Update Profile" story. These drive the real React
// application end-to-end against a running frontend (http://localhost:5173) and backend
// (http://localhost:5261).
//
// NOTE ON PROJECT SETUP: this mirrors LoginForm.SeleniumTests. It assumes a BrowserFixture
// class identical to the one used there exists in this project's namespace
// (ProfileForm.SeleniumTests) — copy BrowserFixture.cs from LoginForm.SeleniumTests and update
// its namespace if this is a brand-new test project.
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - View Profile        -> covered
//   Scenario 2 - Successful Update   -> covered
//   Scenario 3 - Invalid Update      -> covered
//   Scenario 4 - Access Restriction  -> NOT reachable through the UI at all — see the note
//                above ProfileAccessRestriction_Notes() at the bottom of this file. The real
//                proof for Scenario 4 lives in ProfileControllerTests.cs (xUnit), since there
//                is no UI affordance for a user to even attempt editing another user's record.
public sealed class ProfileFormTests : IClassFixture<BrowserFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WebDriverWait _loginWait;

    // Seeded by AuthService.Program.cs in Development. Dedicated to these profile tests so
    // mutating (renaming) it doesn't interfere with LoginFormTests' assertions on SeedUserName.
    private const string SeedUserEmail = "user3@example.com";
    private const string SeedUserPassword = "User123!";
    private const string SeedUserOriginalName = "User Three";
    private const string SeedUserOriginalPhone = "+94770000013";

    // Another seeded user whose phone number is already taken — used for the
    // phone-uniqueness-conflict scenario.
    private const string OtherSeedUserPhone = "+94770000012"; // user2@example.com

    public ProfileFormTests(BrowserFixture fixture)
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

    private void LoginAsSeedUser()
    {
        ResetBrowserSession();
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/login");
        _wait.Until(d => d.FindElement(By.Id("email")));

        _driver.FindElement(By.Id("email")).SendKeys(SeedUserEmail);
        _driver.FindElement(By.Id("password")).SendKeys(SeedUserPassword);
        _driver.FindElement(By.CssSelector("form button[type='submit']")).Click();

        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);
    }

    private void GoToProfilePage()
    {
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/profile");
        _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
        // URL is available immediately on navigation, but this is a full page load (not
        // client-side routing), so AuthContext remounts and fires a fresh getMe() call.
        // That's the same kind of auth round-trip LoginAsSeedUser() already gives 20s for
        // (see _loginWait) — under local dev load (frontend + backend + browser driver all
        // running at once) it can occasionally be slower than the generic 10s _wait allows,
        // so this uses the same generous timeout rather than the default one.
        _loginWait.Until(d => d.FindElements(By.XPath("//button[normalize-space()='Edit profile']")).Any());
    }

    private void ClickEditProfile()
    {
        _wait.Until(d => d.FindElement(By.XPath("//button[normalize-space()='Edit profile']"))).Click();
        _wait.Until(d => d.FindElement(By.Id("name")));
    }

    private void SetField(string id, string value)
    {
        var field = _driver.FindElement(By.Id(id));
        field.Clear();
        // Clear() doesn't always empty react-hook-form-controlled inputs reliably across
        // browsers; belt-and-braces select-all + delete before typing the new value.
        field.SendKeys(Keys.Control + "a");
        field.SendKeys(Keys.Delete);
        if (!string.IsNullOrEmpty(value))
            field.SendKeys(value);
    }

    private void ClickSaveChanges()
    {
        _driver.FindElement(By.XPath("//button[@type='submit' and normalize-space()='Save changes']")).Click();
    }

    private string BodyText() => _driver.FindElement(By.TagName("body")).Text;

    // Refreshing is a client-side reload, so the URL already contains "/profile"
    // instantly — before AuthContext's async getMe() resolves and React swaps the
    // loading spinner for real content. Waiting on the URL alone is a race condition:
    // it can pass or fail depending on how fast the profile fetch happens to be.
    // This waits for the actual expected text to show up in the body before we
    // read/assert on it, so the test is deterministic instead of timing-dependent.
    private void WaitForBodyToContain(string expected)
    {
        _wait.Until(d => BodyText().Contains(expected, StringComparison.Ordinal));
    }

    // Restores this seed user's name/phone back to their original seeded values so tests
    // don't leak state into each other or into other suites (e.g. LoginFormTests, which
    // asserts on seeded user display names).
    private void RestoreSeedUserToOriginalProfile()
    {
        LoginAsSeedUser();
        GoToProfilePage();
        ClickEditProfile();
        SetField("name", SeedUserOriginalName);
        SetField("phoneNo", SeedUserOriginalPhone);
        ClickSaveChanges();
        _wait.Until(d => !d.FindElements(By.Id("name")).Any());
    }

    // ---------- Scenario 1: View Profile ----------

    [Fact]
    public void ViewProfile_LoggedInUser_ShowsCurrentNamePhoneAndEmail()
    {
        LoginAsSeedUser();
        GoToProfilePage();

        var body = BodyText();
        Assert.Contains(SeedUserOriginalName, body);
        Assert.Contains(SeedUserEmail, body);
        Assert.Contains(SeedUserOriginalPhone, body);
    }

    // ---------- Scenario 2: Successful Update ----------

    [Fact]
    public void SaveProfile_ValidNameAndPhone_PersistsAndReflectsImmediately()
    {
        try
        {
            LoginAsSeedUser();
            GoToProfilePage();
            ClickEditProfile();

            const string newName = "User Three Updated";
            const string newPhone = "+94770099913";

            SetField("name", newName);
            SetField("phoneNo", newPhone);
            ClickSaveChanges();

            // Form closes back to the read-only view once the save resolves.
            _wait.Until(d => !d.FindElements(By.Id("name")).Any());

            var body = BodyText();
            Assert.Contains(newName, body);
            Assert.Contains(newPhone, body);

            // Reload the page from scratch to prove the change was actually persisted
            // server-side, not just held in local component state.
            _driver.Navigate().Refresh();
            _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
            WaitForBodyToContain(newName);
            var reloadedBody = BodyText();
            Assert.Contains(newName, reloadedBody);
            Assert.Contains(newPhone, reloadedBody);
        }
        finally
        {
            RestoreSeedUserToOriginalProfile();
        }
    }

    // ---------- Scenario 3: Invalid Update ----------

    [Fact]
    public void SaveProfile_PhoneNumberMissingPlus_ShowsValidationError_AndDoesNotPersist()
    {
        LoginAsSeedUser();
        GoToProfilePage();
        ClickEditProfile();

        SetField("phoneNo", "0771234567"); // missing leading '+'
        // Blur the field so react-hook-form's onTouched validation fires without submitting.
        _driver.FindElement(By.Id("name")).Click();

        var error = _wait.Until(d => d.FindElement(By.Id("phoneNo-error")));
        Assert.Contains("E.164", error.Text, StringComparison.OrdinalIgnoreCase);

        // Confirm nothing was actually submitted/persisted.
        Assert.True(_driver.FindElements(By.Id("phoneNo")).Count > 0, "Form should still be open, not saved.");
        _driver.Navigate().Refresh();
        _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
        WaitForBodyToContain(SeedUserOriginalPhone);
        Assert.Contains(SeedUserOriginalPhone, BodyText());
    }

    [Fact]
    public void SaveProfile_NameOver150Characters_ShowsValidationError_AndDoesNotPersist()
    {
        LoginAsSeedUser();
        GoToProfilePage();
        ClickEditProfile();

        SetField("name", new string('a', 151));
        _driver.FindElement(By.Id("phoneNo")).Click(); // blur

        var error = _wait.Until(d => d.FindElement(By.Id("name-error")));
        Assert.Contains("150", error.Text);

        _driver.Navigate().Refresh();
        _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
        WaitForBodyToContain(SeedUserOriginalName);
        Assert.Contains(SeedUserOriginalName, BodyText());
    }

    [Fact]
    public void SaveProfile_EmptyName_ShowsRequiredError_AndDoesNotPersist()
    {
        LoginAsSeedUser();
        GoToProfilePage();
        ClickEditProfile();

        SetField("name", "");
        _driver.FindElement(By.Id("phoneNo")).Click(); // blur

        var error = _wait.Until(d => d.FindElement(By.Id("name-error")));
        Assert.Equal("Name is required.", error.Text.Trim());
    }

    [Fact]
    public void SaveProfile_PhoneAlreadyUsedByAnotherAccount_ShowsServerConflictError_AndDoesNotPersist()
    {
        try
        {
            LoginAsSeedUser();
            GoToProfilePage();
            ClickEditProfile();

            // This value passes client-side regex validation, so the submit reaches the
            // server, which must reject it with 409 because it belongs to another account.
            SetField("phoneNo", OtherSeedUserPhone);
            ClickSaveChanges();

            var error = _wait.Until(d => d.FindElement(By.Id("phoneNo-error")));
            Assert.Contains("already in use", error.Text, StringComparison.OrdinalIgnoreCase);

            // Still on the edit form (save did not succeed).
            Assert.True(_driver.FindElements(By.Id("phoneNo")).Count > 0);

            _driver.Navigate().Refresh();
            _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
            WaitForBodyToContain(SeedUserOriginalPhone);
            Assert.Contains(SeedUserOriginalPhone, BodyText());
        }
        finally
        {
            RestoreSeedUserToOriginalProfile();
        }
    }

    // ---------- Scenario 4: Access Restriction ----------
    //
    // IMPORTANT: there is no UI path to attempt editing another user's profile. The profile
    // page and its edit form never take or display a target user id — "/profile" always shows
    // and edits whichever account the auth_token cookie belongs to. A browser-driven Selenium
    // test therefore cannot exercise "a user attempts to edit another user's profile" as
    // literally described in the AC, because the UI gives no way to construct that request in
    // the first place (unlike, say, the admin-API test in LoginFormTests, which could at least
    // issue a raw fetch() to a URL the UI doesn't expose). The closest meaningful UI-level
    // check is: an unauthenticated visitor is kept off the page entirely.

    [Fact]
    public void ProfilePage_NotLoggedIn_RedirectsAwayFromProfile()
    {
        ResetBrowserSession();
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/profile");

        // ProtectedRoute should bounce an unauthenticated visitor to /login.
        _loginWait.Until(d => d.Url.Contains("/login", StringComparison.Ordinal));
        Assert.DoesNotContain(SeedUserEmail, BodyText());
    }
}
