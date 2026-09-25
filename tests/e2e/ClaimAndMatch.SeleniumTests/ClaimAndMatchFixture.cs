using MySqlConnector;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;

namespace ClaimAndMatch.SeleniumTests;

// Shared Selenium fixture for Story 2+: one Chrome session switched between two seeded accounts,
// plus a direct MySQL connection for test setup the UI can't do.
public sealed class ClaimAndMatchFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";

    public const string UserAEmail = "user1@example.com";
    public const string UserAPassword = "User123!";
    public const string UserBEmail = "user2@example.com";
    public const string UserBPassword = "User123!";

    // Local dev MySQL, cross-database via fully-qualified table names on one connection.
    private const string ConnectionString =
        "Server=localhost;Port=3306;Uid=root;Pwd=yourpassword;AllowUserVariables=true;";

    public IWebDriver Driver { get; }
    public WebDriverWait Wait { get; }

    // Fixed, reused across every class/run (parallelization is disabled assembly-wide, so there's
    // never more than one Chrome instance open against it at once) - this is what lets a real login
    // in one test class carry over to the next instead of re-hitting AuthService's login limiter.
    private static readonly string ProfileDir =
        Path.Combine(Path.GetTempPath(), "ClaimAndMatchSeleniumProfile");

    public ClaimAndMatchFixture()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-features=PasswordLeakDetection");
        options.AddArgument($"--user-data-dir={ProfileDir}");
        options.AddUserProfilePreference("credentials_enable_service", false);
        options.AddUserProfilePreference("profile.password_manager_enabled", false);
        options.AddUserProfilePreference("profile.password_manager_leak_detection", false);

        Driver = new ChromeDriver(options);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }

    // ---- Auth ------------------------------------------------------------

    private const string AuthServiceUrl = "http://localhost:5261";

    private const string TokenStorageKeyPrefix = "seleniumCachedToken:";

    // Token cache lives in the browser's own localStorage (persisted via ProfileDir), not an
    // in-memory field - so it survives across fixture instances/classes, not just within one.
    public void LoginAs(string email, string password)
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        var cachedToken = GetCachedToken(email);
        if (cachedToken is not null && TryUseCachedToken(cachedToken))
        {
            return;
        }

        Driver.Manage().Cookies.DeleteAllCookies();
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        Wait.Until(d => d.FindElement(By.Id("email"))).SendKeys(email);
        Driver.FindElement(By.Id("password")).SendKeys(password);
        Driver.FindElement(By.CssSelector("button[type='submit']")).Click();

        Wait.Until(d => !d.Url.Contains("/login", StringComparison.Ordinal));

        var token = Driver.Manage().Cookies.GetCookieNamed("auth_token")?.Value
            ?? throw new InvalidOperationException("Login succeeded but no auth_token cookie was found.");
        CacheToken(email, token);

        // Re-arms AuthContext's GET /api/auth/me across the cross-port cookie-delivery gap.
        SyncAuthCookieToAuthService(token);
    }

    private bool TryUseCachedToken(string token)
    {
        Driver.Manage().Cookies.DeleteAllCookies();
        Driver.Manage().Cookies.AddCookie(new Cookie(
            "auth_token", token, "localhost", "/", DateTime.UtcNow.AddHours(1)));
        SyncAuthCookieToAuthService(token);
        Driver.Navigate().GoToUrl($"{BaseUrl}/home");

        try
        {
            Wait.Until(d => !d.Url.Contains("/login", StringComparison.Ordinal));
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            return false;
        }
    }

    private string? GetCachedToken(string email)
    {
        var js = (IJavaScriptExecutor)Driver;
        return js.ExecuteScript(
            "return window.localStorage.getItem(arguments[0]);",
            TokenStorageKeyPrefix + email) as string;
    }

    private void CacheToken(string email, string token)
    {
        var js = (IJavaScriptExecutor)Driver;
        js.ExecuteScript(
            "window.localStorage.setItem(arguments[0], arguments[1]);",
            TokenStorageKeyPrefix + email, token);
    }

    private void SyncAuthCookieToAuthService(string authToken)
    {
        if (Driver is not ChromiumDriver chrome) return;

        chrome.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
        chrome.ExecuteCdpCommand("Network.setCookie", new Dictionary<string, object>
        {
            { "name", "auth_token" },
            { "value", authToken },
            { "domain", "localhost" },
            { "path", "/" },
            { "httpOnly", true },
            { "secure", false },
            { "sameSite", "Lax" },
            { "url", AuthServiceUrl }
        });
    }

    // ---- Report creation (real wizard, real Kafka event, real worker) --

    public Guid CreateLostReport(
        string title,
        string category,
        string description,
        string location,
        bool withPhoto,
        Guid ownerUserId)
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/report-lost-item");
        Wait.Until(d => d.FindElement(By.Id("title")));

        Driver.FindElement(By.Id("title")).SendKeys(title);
        SelectCategory(category);
        Driver.FindElement(By.Id("description")).SendKeys(description);
        ClickContinue();

        SetDate("Date lost", DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        Wait.Until(d => d.FindElement(By.Id("lastKnownLocation"))).SendKeys(location);
        ClickContinue();

        Wait.Until(d => d.FindElement(By.Id("hiddenInformation")))
            .SendKeys("Selenium (LF-173) fixture report.");

        if (withPhoto)
        {
            Driver.FindElement(By.CssSelector("input[type=file]")).SendKeys(TestFiles.PathTo("sample.jpg"));
        }

        Driver.FindElement(By.XPath("//button[contains(text(),'Report Lost Item')]")).Click();
        Wait.Until(d => d.Url.Contains("/report-lost-item/success", StringComparison.Ordinal));

        return GetLatestItemId(ownerUserId, "LOST");
    }

    public Guid CreateFoundReport(
        string title,
        string category,
        string description,
        string location,
        bool withPhoto,
        Guid ownerUserId)
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/report-found-item");
        Wait.Until(d => d.FindElement(By.Id("title")));

        Driver.FindElement(By.Id("title")).SendKeys(title);
        SelectCategory(category);
        Driver.FindElement(By.Id("description")).SendKeys(description);
        ClickContinue();

        SetDate("Date found", DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        Wait.Until(d => d.FindElement(By.Id("locationFound"))).SendKeys(location);
        ClickContinue();

        Wait.Until(d => d.FindElement(By.Id("hiddenInformation")))
            .SendKeys("Selenium (LF-173) fixture report.");

        if (withPhoto)
        {
            Driver.FindElement(By.CssSelector("input[type=file]")).SendKeys(TestFiles.PathTo("sample.jpg"));
        }

        Driver.FindElement(By.XPath("//button[contains(text(),'Report Found Item')]")).Click();
        Wait.Until(d => d.Url.Contains("/report-found-item/success", StringComparison.Ordinal));

        return GetLatestItemId(ownerUserId, "FOUND");
    }

    private void SelectCategory(string category)
    {
        Driver.FindElement(By.Id("category")).Click();
        Wait.Until(d => d.FindElement(By.XPath($"//button[normalize-space()='{category}']"))).Click();
        Wait.Until(d => d.FindElement(By.Id("category")).Text.Contains(category, StringComparison.Ordinal));
    }

    private void SetDate(string ariaLabel, string date)
    {
        var selector = By.CssSelector($"input[aria-label='{ariaLabel}']");
        var dateInput = Wait.Until(d => d.FindElement(selector));

        ((IJavaScriptExecutor)Driver).ExecuteScript(
            "const input = arguments[0];" +
            "const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "valueSetter.call(input, arguments[1]);" +
            "input.dispatchEvent(new Event('input', { bubbles: true }));" +
            "input.dispatchEvent(new Event('change', { bubbles: true }));",
            dateInput,
            date);

        Wait.Until(d => d.FindElement(selector).GetAttribute("value") == date);
    }

    private void ClickContinue()
    {
        Wait.Until(d =>
        {
            try
            {
                var button = d.FindElement(By.XPath("//button[contains(text(),'Continue')]"));
                if (!button.Displayed || !button.Enabled) return false;

                button.Click();
                return true;
            }
            catch (StaleElementReferenceException)
            {
                return false;
            }
        });
    }

    // ---- Direct MySQL (test wiring only - never part of the flow under test) -------------------

    public Guid GetUserId(string email)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT id FROM auth_service.users WHERE email = @email LIMIT 1;", connection);
        command.Parameters.AddWithValue("@email", email);

        var result = command.ExecuteScalar()
            ?? throw new InvalidOperationException($"No auth_service user found for '{email}'.");

        return (Guid)result;
    }

    // The newest report owned by this user, of the given type - the wizard's success page shows no id.
    public Guid GetLatestItemId(Guid ownerUserId, string itemType)
    {
        var table = itemType == "LOST" ? "item_service.lost_items" : "item_service.found_items";

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            $"SELECT id FROM {table} WHERE user_id = @userId ORDER BY created_at DESC LIMIT 1;", connection);
        command.Parameters.AddWithValue("@userId", ownerUserId);

        var result = command.ExecuteScalar()
            ?? throw new InvalidOperationException(
                $"No {itemType} report found for user '{ownerUserId}'. Did report creation fail?");

        return (Guid)result;
    }

    // Waits for the real pipeline's row to exist, then patches it to the requested state.
    public void ForceImageDescriptionState(
        Guid itemId,
        string itemType,
        string status,
        int attempts,
        string description,
        string attributesJson)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        WaitForRowToExist(connection, itemId, itemType);

        using var command = new MySqlCommand(
            """
            UPDATE matching_service.image_descriptions
            SET processing_status = @status,
                attempts = @attempts,
                next_retry_at = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                description = @description,
                attributes_json = @attributesJson,
                error_code = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE item_id = @itemId
              AND item_type = @itemType
              AND is_superseded = 0;
            """,
            connection);

        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@description", description);
        command.Parameters.AddWithValue("@attributesJson", attributesJson);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@itemType", itemType);

        command.ExecuteNonQuery();
    }

    private static void WaitForRowToExist(MySqlConnection connection, Guid itemId, string itemType)
    {
        const string sql = """
            SELECT COUNT(*) FROM matching_service.image_descriptions
            WHERE item_id = @itemId AND item_type = @itemType AND is_superseded = 0;
            """;

        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@itemId", itemId);
            command.Parameters.AddWithValue("@itemType", itemType);

            if (Convert.ToInt64(command.ExecuteScalar()) > 0) return;

            Thread.Sleep(500);
        }

        throw new InvalidOperationException(
            $"No image_descriptions row appeared for {itemType} item '{itemId}' within 20s. " +
            "Confirm the photo upload succeeded and MatchingService's Kafka consumer is running.");
    }

    // The newest match involving this user, on either side.
    public Guid GetLatestMatchId(Guid userId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            SELECT id FROM matching_service.matches
            WHERE lost_reporter_id = @userId OR finder_id = @userId
            ORDER BY created_at DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@userId", userId);

        var result = command.ExecuteScalar()
            ?? throw new InvalidOperationException($"No match found involving user '{userId}'.");

        return (Guid)result;
    }

    // ---- Story 3 (matches list/review page) test wiring only --------------------------------

    // Inserts a matches row directly, for statuses/flags no real flow needs to produce for these tests.
    public Guid InsertMatchDirectly(
        Guid lostReporterId,
        Guid finderId,
        string status,
        string lostTitle,
        string foundTitle,
        bool isActive = true,
        Guid? claimantId = null)
    {
        var id = Guid.NewGuid();
        var lostSnapshot = $$"""{"id":"{{Guid.NewGuid()}}","type":"LOST","title":"{{lostTitle}}","category":"Other","description":"Seeded directly for a Story 3 browser test.","date":"2026-09-01","location":"Test location"}""";
        var foundSnapshot = $$"""{"id":"{{Guid.NewGuid()}}","type":"FOUND","title":"{{foundTitle}}","category":"Other","description":"Seeded directly for a Story 3 browser test.","date":"2026-09-01","location":"Test location"}""";

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO matching_service.matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @claimantId,
                'LOST', @status, @isActive, 75.00, 'text-v1',
                @lostSnapshot, @foundSnapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@foundItemId", Guid.NewGuid());
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@claimantId", claimantId ?? lostReporterId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@isActive", isActive);
        command.Parameters.AddWithValue("@lostSnapshot", lostSnapshot);
        command.Parameters.AddWithValue("@foundSnapshot", foundSnapshot);

        command.ExecuteNonQuery();
        return id;
    }

    // ---- Story 7 test wiring only ------------------------------------------------------------

    // True once ItemLifecycleConsumer has processed a resolve/delete event for this item.
    public bool IsItemInactive(Guid itemId, string itemType)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            SELECT inactive_reason FROM matching_service.matching_item_states
            WHERE item_id = @itemId AND item_type = @itemType;
            """, connection);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@itemType", itemType);

        var result = command.ExecuteScalar();
        return result is not null and not DBNull;
    }

    // The match's own deactivation_reason column, or null if still active.
    public string? GetMatchDeactivationReason(Guid matchId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT deactivation_reason FROM matching_service.matches WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", matchId);

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    private (Guid UserId, string Email, string Password)? _cachedFreshUser;

    // Registers one new account on first call, reusing it for every later caller this run - avoids
    // the register rate limiter.
    public (Guid UserId, string Email, string Password) GetOrRegisterFreshVerifiedUser()
    {
        return _cachedFreshUser ??= RegisterFreshVerifiedUser();
    }

    // Registers through the real /register form, then marks it verified directly (skips the real
    // Mailtrap click). Call GetOrRegisterFreshVerifiedUser() instead of this directly.
    private (Guid UserId, string Email, string Password) RegisterFreshVerifiedUser()
    {
        var email = $"selenium.story3.{Guid.NewGuid():N}@example.com";
        const string password = "Str0ngPass1";
        var digits = Random.Shared.Next(100000000, 999999999);

        Driver.Navigate().GoToUrl(BaseUrl);
        Driver.Manage().Cookies.DeleteAllCookies();
        Driver.Navigate().GoToUrl($"{BaseUrl}/register");

        try
        {
            Wait.Until(d =>
            {
                try
                {
                    var name = d.FindElement(By.Id("name"));
                    name.Clear();
                    name.SendKeys("Selenium Story 3 User");

                    var emailField = d.FindElement(By.Id("email"));
                    emailField.Clear();
                    emailField.SendKeys(email);

                    var phone = d.FindElement(By.Id("phoneNo"));
                    phone.Clear();
                    phone.SendKeys($"+94{digits}");

                    var passwordField = d.FindElement(By.Id("password"));
                    passwordField.Clear();
                    passwordField.SendKeys(password);

                    var confirmPassword = d.FindElement(By.Id("confirmPassword"));
                    confirmPassword.Clear();
                    confirmPassword.SendKeys(password);

                    d.FindElement(By.CssSelector("button[type='submit']")).Click();
                    return true;
                }
                catch (NoSuchElementException)
                {
                    return false;
                }
                catch (StaleElementReferenceException)
                {
                    return false;
                }
            });
        }
        catch (WebDriverTimeoutException ex)
        {
            var snippet = Driver.PageSource.Length > 500 ? Driver.PageSource[..500] : Driver.PageSource;
            throw new InvalidOperationException(
                $"Register form fields never all appeared within 15s. Url: {Driver.Url}\nPage source (first 500 chars):\n{snippet}",
                ex);
        }

        Wait.Until(d => d.Url.Contains("/verify-email", StringComparison.Ordinal));

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "UPDATE auth_service.users SET is_email_verified = 1 WHERE email = @email;", connection);
        command.Parameters.AddWithValue("@email", email);
        command.ExecuteNonQuery();

        return (GetUserId(email), email, password);
    }
}
