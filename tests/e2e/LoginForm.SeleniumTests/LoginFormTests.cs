using System.Text;
using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace LoginForm.SeleniumTests;

// Selenium UI tests for the User and Admin Login story.
// These drive the real React application and verify what a browser/user experiences.
public sealed class LoginFormTests : IClassFixture<BrowserFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private readonly WebDriverWait _loginWait;

    // These are seeded by AuthService.Program.cs in Development.
    private const string SeedUserEmail = "user1@example.com";
    private const string SeedUserPassword = "User123!";
    private const string SeedUserName = "User One";

    private const string SeedAdminEmail = "admin1@lostandfound.com";
    private const string SeedAdminPassword = "Admin123!";
    private const string SeedAdminName = "Admin One";

    public LoginFormTests(BrowserFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(20));
        _loginWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(20));
    }

    // ---------- Common browser/session helpers ----------

    private void ResetBrowserSession()
    {
        // Visit the SPA first so local/session storage is available on the correct origin.
        _driver.Navigate().GoToUrl(BrowserFixture.BaseUrl);
        _driver.Manage().Cookies.DeleteAllCookies();

        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "window.localStorage.clear(); window.sessionStorage.clear();");

        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/login");
        _wait.Until(d => d.FindElement(By.Id("email")));
    }

    private void FillLogin(string email, string password)
    {
        var emailBox = _driver.FindElement(By.Id("email"));
        emailBox.Clear();
        emailBox.SendKeys(email);

        var passwordBox = _driver.FindElement(By.Id("password"));
        passwordBox.Clear();
        passwordBox.SendKeys(password);
    }

    private void SubmitLogin()
    {
        _driver.FindElement(By.CssSelector("form button[type='submit']")).Click();
    }

    private void Login(string email, string password)
    {
        ResetBrowserSession();
        FillLogin(email, password);
        SubmitLogin();
    }

    private IWebElement WaitForAlert()
    {
        return _wait.Until(d => d.FindElement(By.CssSelector("[role='alert']")));
    }

    private Cookie? GetAuthCookie()
    {
        return _driver.Manage().Cookies.GetCookieNamed("auth_token");
    }

    private JsonElement ReadJwtPayload(Cookie cookie)
    {
        var parts = cookie.Value.Split('.');
        Assert.Equal(3, parts.Length);

        var payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/');

        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static long GetUnixTimeSeconds()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void AssertAuthenticatedUserOnHome(string expectedName)
    {
        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);
        Assert.Equal(BrowserFixture.BaseUrl, _driver.Url.TrimEnd('/'));

        var bodyText = _driver.FindElement(By.TagName("body")).Text;
        Assert.Contains(expectedName, bodyText);
        Assert.Contains("Sign out", bodyText);
    }

    private void AssertAdminDashboardLoaded(string expectedAdminName)
    {
        // App.tsx routes AdminDashboardPage -> /admin/dashboard, and that page immediately
        // redirects to /admin/users. The User Management page is therefore the actual
        // rendered admin dashboard destination in this build.
        _loginWait.Until(d => d.Url.Contains("/admin/users", StringComparison.Ordinal));

        var bodyText = _driver.FindElement(By.TagName("body")).Text;
        Assert.Contains("User Management", bodyText);
        Assert.Contains(expectedAdminName, bodyText);
        Assert.Contains("Admin Dashboard", bodyText);
        Assert.Contains("Sign out", bodyText);
        Assert.Contains("Admin", bodyText);
    }

    // ---------- Scenario 1: Successful User Login ----------

    [Fact]
    public void UserLogin_ValidVerifiedUser_RedirectsToUserDashboard_AndSetsJwtCookie()
    {
        Login(SeedUserEmail, SeedUserPassword);

        AssertAuthenticatedUserOnHome(SeedUserName);

        var cookie = GetAuthCookie();
        Assert.NotNull(cookie);
        Assert.True(cookie!.IsHttpOnly);

        var payload = ReadJwtPayload(cookie);

        Assert.Equal(SeedUserEmail, payload.GetProperty("email").GetString());
        Assert.Equal("0", payload.GetProperty("is_admin").GetString());
        Assert.Equal("1", payload.GetProperty("email_verified").GetString());

        var sub = payload.GetProperty("sub").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sub));
        Assert.True(Guid.TryParse(sub, out _));

        var exp = payload.GetProperty("exp").GetInt64();
        Assert.True(exp > GetUnixTimeSeconds(), "JWT expiry must be in the future.");

        // The frontend never receives the JWT in the login response body. The browser uses
        // the httpOnly cookie and the /api/auth/me call succeeds, proving the service accepts
        // the signed token for an authenticated request.
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/profile");
        _wait.Until(d => d.Url.Contains("/profile", StringComparison.Ordinal));
        Assert.Contains(SeedUserName, _driver.FindElement(By.TagName("body")).Text);
        Assert.Contains(SeedUserEmail, _driver.FindElement(By.TagName("body")).Text);
    }

    // ---------- Scenario 2: Successful Admin Login ----------

    [Fact]
    public void AdminLogin_ValidAdmin_RedirectsToAdminDashboard_AndJwtContainsAdminClaim()
    {
        Login(SeedAdminEmail, SeedAdminPassword);

        AssertAdminDashboardLoaded(SeedAdminName);

        var cookie = GetAuthCookie();
        Assert.NotNull(cookie);
        Assert.True(cookie!.IsHttpOnly);

        var payload = ReadJwtPayload(cookie);

        Assert.Equal(SeedAdminEmail, payload.GetProperty("email").GetString());
        Assert.Equal("1", payload.GetProperty("is_admin").GetString());
        Assert.Equal("1", payload.GetProperty("email_verified").GetString());

        var sub = payload.GetProperty("sub").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sub));
        Assert.True(Guid.TryParse(sub, out _));

        var exp = payload.GetProperty("exp").GetInt64();
        Assert.True(exp > GetUnixTimeSeconds(), "JWT expiry must be in the future.");
    }

    // ---------- Scenario 3: Invalid Credentials ----------

    [Theory]
    [InlineData("does-not-exist-selenium@example.com", "WrongPassword1!")]
    [InlineData(SeedUserEmail, "DefinitelyWrongPassword1!")]
    public void Login_InvalidCredentials_ShowsSameGenericError(string email, string password)
    {
        Login(email, password);

        var alert = WaitForAlert();
        Assert.Equal("Invalid email or password.", alert.Text.Trim());

        Assert.DoesNotContain("/profile", _driver.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/admin", _driver.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/verify-email", _driver.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Null(GetAuthCookie());
    }

    // ---------- Scenario 4: Deleted Account ----------

    [Fact]
    public void Login_DeletedAccount_IsRejected_WithNonRevealingError()
    {
        // Configure these to an account that has already been soft-deleted in the test DB.
        // The repository deliberately excludes deleted_at != NULL users from GetByEmailAsync,
        // so the UI should see the same generic error as an unknown email.
        var email = RequiredEnvironmentVariable("SELENIUM_DELETED_USER_EMAIL");
        var password = RequiredEnvironmentVariable("SELENIUM_DELETED_USER_PASSWORD");

        Login(email, password);

        var alert = WaitForAlert();
        Assert.Equal("Invalid email or password.", alert.Text.Trim());
        Assert.Null(GetAuthCookie());
        Assert.DoesNotContain("/admin", _driver.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/profile", _driver.Url, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Scenario 5: Unverified Account ----------

    [Fact]
    public void UnverifiedUserLogin_IsRejected_AndRedirectedToVerifyEmail_WithNoAuthCookie()
    {
        // Create a fresh unverified account through the real registration UI. Do NOT enter
        // the OTP. This keeps the setup fully browser-driven and avoids a hard-coded account.
        var email = $"selenium.unverified.{Guid.NewGuid():N}@example.com";
        const string password = "Str0ngPass1";

        RegisterWithoutVerifying(email, password);

        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/login");
        _wait.Until(d => d.FindElement(By.Id("email")));

        FillLogin(email, password);
        SubmitLogin();

        _loginWait.Until(d => d.Url.Contains("/verify-email", StringComparison.Ordinal));

        Assert.Null(GetAuthCookie());
        Assert.DoesNotContain("/profile", _driver.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/admin", _driver.Url, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Scenario 6: Unauthorized Admin Route Access ----------

    [Fact]
    public void RegularUser_AdminFrontendRoute_IsDeniedByClientRouteGuard()
    {
        Login(SeedUserEmail, SeedUserPassword);
        AssertAuthenticatedUserOnHome(SeedUserName);

        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/admin/users");

        // ProtectedRoute has roles=["Admin"] and sends regular users to "/".
        _loginWait.Until(d => d.Url.TrimEnd('/') == BrowserFixture.BaseUrl);
        Assert.Equal(BrowserFixture.BaseUrl, _driver.Url.TrimEnd('/'));
        Assert.Contains(SeedUserName, _driver.FindElement(By.TagName("body")).Text);
        Assert.Contains("Sign out", _driver.FindElement(By.TagName("body")).Text);

        // No admin page should be rendered in the regular user's browser.
        Assert.DoesNotContain("User Management", _driver.FindElement(By.TagName("body")).Text);
    }

    [Fact]
    public void RegularUser_AdminApiRequest_MustReturn403()
    {
        Login(SeedUserEmail, SeedUserPassword);
        AssertAuthenticatedUserOnHome(SeedUserName);

        // This performs the actual cross-service admin endpoint call from the authenticated
        // browser context. It sends the httpOnly auth cookie with credentials: include.
        // The story requires 403 for a normal user's admin-only route/API.
        var status = ExecuteAuthenticatedFetchStatus(
            $"{BrowserFixture.AuthApiBaseUrl}/api/admin/users");

        Assert.Equal(403, status);
    }

    // ---------- Additional useful login-form validation ----------

    [Fact]
    public void Login_EmptyRequiredFields_ShowsInlineValidationErrors_AndDoesNotSubmit()
    {
        ResetBrowserSession();
        SubmitLogin();

        var emailError = _wait.Until(d => d.FindElement(By.Id("email-error")));
        var passwordError = _wait.Until(d => d.FindElement(By.Id("password-error")));

        Assert.Equal("Email is required.", emailError.Text.Trim());
        Assert.Equal("Password is required.", passwordError.Text.Trim());
        Assert.Null(GetAuthCookie());
        Assert.Contains("/login", _driver.Url, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Setup helpers ----------

    private void RegisterWithoutVerifying(string email, string password)
    {
        ResetBrowserSession();
        _driver.Navigate().GoToUrl($"{BrowserFixture.BaseUrl}/register");
        _loginWait.Until(d => d.FindElement(By.Id("email")));

        _driver.FindElement(By.Id("name")).SendKeys("Selenium Unverified Login User");
        _driver.FindElement(By.Id("email")).SendKeys(email);
        _driver.FindElement(By.Id("phoneNo")).SendKeys(UniquePhone());
        _driver.FindElement(By.Id("password")).SendKeys(password);
        _driver.FindElement(By.Id("confirmPassword")).SendKeys(password);
        _driver.FindElement(By.CssSelector("button[type='submit']")).Click();

        _loginWait.Until(d => d.Url.Contains("/verify-email", StringComparison.Ordinal));
    }

    private static string UniquePhone()
    {
        var local = Random.Shared.Next(100000000, 999999999);
        return $"+94{local}";
    }

    private int ExecuteAuthenticatedFetchStatus(string url)
    {
        var script = """
            const url = arguments[0];
            const done = arguments[arguments.length - 1];
            fetch(url, { method: 'GET', credentials: 'include' })
              .then(r => done(r.status))
              .catch(() => done(-1));
            """;

        var result = ((IJavaScriptExecutor)_driver).ExecuteAsyncScript(script, url);
        return Convert.ToInt32(result);
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required Selenium test environment variable '{name}' is not set.");
        }

        return value;
    }
}
