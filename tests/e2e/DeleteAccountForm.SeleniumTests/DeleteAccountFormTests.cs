using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace DeleteAccountForm.SeleniumTests;

// Selenium UI tests for the "Delete Account" story. These drive the real React
// application end-to-end against a running frontend (http://localhost:5173) and backend
// (http://localhost:5261).
//
// PROJECT SETUP: this mirrors ProfileForm.SeleniumTests / LoginForm.SeleniumTests. This
// file, DeleteAccountForm.SeleniumTests.csproj, and BrowserFixture.cs already live together
// in their own project folder — drop the folder alongside your other *.SeleniumTests
// projects as-is.
//
// NOTE ON TEST-USER CONSUMPTION: unlike Login/Profile, a *successful* deletion is
// irreversible and the AC explicitly requires proving the account can no longer log in
// afterwards. That means DeleteAccount_ValidPassword_SucceedsAndAccountCanNoLongerLogIn
// permanently consumes its seed user (user15@example.com) — it cannot be restored the way
// ProfileFormTests restores a renamed profile. Re-running the full suite against the same
// database will fail that one test on the second run (the account is already gone). Re-seed
// the dev database (or provide a fresh throwaway account) before each run. The other two
// tests below intentionally do NOT complete a deletion, so their users (user16, user17) are
// reusable across runs.
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - Successful Deletion         -> covered
//   Scenario 2 - Confirmation Required        -> covered, but see the IMPORTANT note above
//                DeleteAccount_PageRequiresPassword_AndShowsConsequenceWarning below: there
//                is no separate "confirmation dialog" step in the actual UI.
//   Scenario 3 - Incorrect Password on Confirmation -> covered
//   Scenario 4 - Post-Deletion Login Attempt  -> covered as part of Scenario 1's test, since
//                that's the only way to prove deletion actually happened server-side.
public sealed class DeleteAccountFormTests : IClassFixture<BrowserFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WebDriverWait _loginWait;

    // Seeded by AuthService.Program.cs in Development. Dedicated to these delete-account
    // tests so they don't collide with the seed users LoginFormTests/ProfileFormTests/
    // ResetPasswordFormTests already depend on (user1-user4).
    private const string DeletableUserEmail = "user15@example.com";
    private const string DeletableUserPassword = "User123!";

    private const string WrongPasswordUserEmail = "user16@example.com";
    private const string WrongPasswordUserPassword = "User123!";

    private const string WarningCheckUserEmail = "user17@example.com";
    private const string WarningCheckUserPassword = "User123!";

    public DeleteAccountFormTests(BrowserFixture fixture)
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

        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);
    }

    private void GoToProfilePage()
    {
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/profile");
        _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
        // Same reasoning as ProfileFormTests.GoToProfilePage: full page load means
        // AuthContext remounts and fires a fresh getMe() call, so wait generously for the
        // Delete Account section (which only renders for a resolved, non-admin user).
        _loginWait.Until(d => d.FindElements(By.Id("delete-password")).Any());
    }

    private IWebElement DeletePasswordField() => _driver.FindElement(By.Id("delete-password"));

    private IWebElement DeleteAccountButton() =>
        _driver.FindElement(By.XPath("//button[normalize-space()='Delete account']"));

    private IWebElement WaitForAlert() => _wait.Until(d => d.FindElement(By.CssSelector("[role='alert']")));

    private string BodyText() => _driver.FindElement(By.TagName("body")).Text;

    // ---------- Scenario 1: Successful Deletion (+ Scenario 4: Post-Deletion Login Attempt) ----------

    [Fact]
    public void DeleteAccount_ValidPassword_SucceedsAndAccountCanNoLongerLogIn()
    {
        LoginAs(DeletableUserEmail, DeletableUserPassword);
        GoToProfilePage();

        DeletePasswordField().SendKeys(DeletableUserPassword);
        Assert.False(DeleteAccountButton().GetAttribute("disabled") is not null);
        DeleteAccountButton().Click();

        // On success the app logs out client-side and redirects to /login.
        _loginWait.Until(d => d.Url.Contains("/login", StringComparison.Ordinal));
        Assert.Null(_driver.Manage().Cookies.GetCookieNamed("auth_token"));

        // Scenario 4: the same credentials must now be rejected.
        _wait.Until(d => d.FindElement(By.Id("email")));
        _driver.FindElement(By.Id("email")).SendKeys(DeletableUserEmail);
        _driver.FindElement(By.Id("password")).SendKeys(DeletableUserPassword);
        _driver.FindElement(By.CssSelector("form button[type='submit']")).Click();

        var error = WaitForAlert();
        Assert.Contains("Invalid email or password", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_driver.Url, "/profile", StringComparison.Ordinal);
        Assert.Null(_driver.Manage().Cookies.GetCookieNamed("auth_token"));
    }

    // ---------- Scenario 2: Confirmation Required ----------
    //
    // IMPORTANT: the AC describes clicking "Delete Account" to open a confirmation dialog
    // that then demands the password. The actual implementation renders the password field,
    // the "This action cannot be undone" warning, and the (disabled-until-filled) "Delete
    // account" button all together on page load — there's no separate dialog step to click
    // through first. Functionally the two hard requirements from the AC still hold (password
    // is mandatory before deletion proceeds; the consequence warning is shown), so this test
    // verifies those, while flagging the dialog-vs-inline-section discrepancy for the team.

    [Fact]
    public void DeleteAccount_PageRequiresPassword_AndShowsConsequenceWarning()
    {
        LoginAs(WarningCheckUserEmail, WarningCheckUserPassword);
        GoToProfilePage();

        var body = BodyText();
        Assert.Contains("Delete account", body);
        Assert.Contains("cannot be undone", body, StringComparison.OrdinalIgnoreCase);

        // Password is empty -> the button must be disabled, so deletion cannot proceed
        // without it.
        Assert.True(DeleteAccountButton().GetAttribute("disabled") is not null);

        DeletePasswordField().SendKeys("SomeCharacters");
        Assert.False(DeleteAccountButton().GetAttribute("disabled") is not null);

        // Never actually click delete here — this test intentionally leaves the account
        // untouched so it's reusable across runs.
    }

    // ---------- Scenario 3: Incorrect Password on Confirmation ----------

    [Fact]
    public void DeleteAccount_WrongPassword_ShowsError_AndAccountIsNotDeleted()
    {
        LoginAs(WrongPasswordUserEmail, WrongPasswordUserPassword);
        GoToProfilePage();

        DeletePasswordField().SendKeys("DefinitelyWrongPassword1!");
        DeleteAccountButton().Click();

        var error = WaitForAlert();
        Assert.Contains("Incorrect password", error.Text, StringComparison.OrdinalIgnoreCase);

        // Still on the profile page — nothing was deleted.
        Assert.Contains("/profile", _driver.Url);
        Assert.NotNull(_driver.Manage().Cookies.GetCookieNamed("auth_token"));

        // Prove the account genuinely still exists by logging in again from scratch.
        LoginAs(WrongPasswordUserEmail, WrongPasswordUserPassword);
        Assert.Equal(BrowserFixture.BaseUrl.TrimEnd('/'), _driver.Url.TrimEnd('/'));
    }
}
