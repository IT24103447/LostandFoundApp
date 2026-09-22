using MySqlConnector;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;

namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Story 2 (LF-173) browser fixture. Shared across every test in the class: one Chrome session
/// (switched between the two real seeded accounts via the real /login form, not cookie injection),
/// plus a direct MySQL connection used ONLY for two things the real UI/pipeline cannot give us right
/// now:
///   1. Reading back the id of a report just created through the real wizard (the success page shows
///      no id), by querying "the newest report owned by this user".
///   2. Forcing an image_descriptions row into a COMPLETED or permanently-parked-PENDING state, because
///      real Gemini analysis is currently blocked by a confirmed upstream 503 "high demand" outage (see
///      Bugs_Sprint3.md, "Known External Dependency Issue #1") and cannot be waited on reliably. The
///      report and its photo are still uploaded for real through the real wizard/Kafka/worker pipeline;
///      only the one dependency Google is currently failing gets patched.
///
/// Both seeded accounts are part of AuthService's own Development seed data (Program.cs
/// SeedUsersAsync) - already verified, no registration/email-verification needed:
///   user1@example.com / User123!  (owns the pre-existing item reports from earlier manual QA)
///   user2@example.com / User123!  (owns none - a clean second party for the cross-user claim flow)
/// </summary>
public sealed class ClaimAndMatchFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";

    public const string UserAEmail = "user1@example.com";
    public const string UserAPassword = "User123!";
    public const string UserBEmail = "user2@example.com";
    public const string UserBPassword = "User123!";

    // Local dev MySQL (docker container "mysql-local"), same credentials every service's
    // appsettings/user-secrets already use. Cross-database queries via fully-qualified table names
    // on one connection, rather than one connection per service database.
    private const string ConnectionString =
        "Server=localhost;Port=3306;Uid=root;Pwd=yourpassword;AllowUserVariables=true;";

    public IWebDriver Driver { get; }
    public WebDriverWait Wait { get; }

    public ClaimAndMatchFixture()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-features=PasswordLeakDetection");
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

    // AuthService rate-limits /api/auth/login to 10 requests per 5 minutes (Program.cs). With two
    // accounts and several tests each switching users, a real login per switch blows through that
    // window fast. Same fix Story 1's wizard tests already use: log in for real once per account,
    // then reuse that auth_token cookie on every later switch back to the same account.
    private readonly Dictionary<string, string> _tokensByEmail = new();

    public void LoginAs(string email, string password)
    {
        Driver.Navigate().GoToUrl(BaseUrl);
        Driver.Manage().Cookies.DeleteAllCookies();

        if (_tokensByEmail.TryGetValue(email, out var cachedToken))
        {
            Driver.Manage().Cookies.AddCookie(new Cookie(
                "auth_token", cachedToken, "localhost", "/", DateTime.UtcNow.AddHours(1)));
            SyncAuthCookieToAuthService(cachedToken);
            Driver.Navigate().GoToUrl($"{BaseUrl}/home");
            Wait.Until(d => !d.Url.Contains("/login", StringComparison.Ordinal));
            return;
        }

        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        Wait.Until(d => d.FindElement(By.Id("email"))).SendKeys(email);
        Driver.FindElement(By.Id("password")).SendKeys(password);
        Driver.FindElement(By.CssSelector("button[type='submit']")).Click();

        Wait.Until(d => !d.Url.Contains("/login", StringComparison.Ordinal));

        var token = Driver.Manage().Cookies.GetCookieNamed("auth_token")?.Value
            ?? throw new InvalidOperationException("Login succeeded but no auth_token cookie was found.");
        _tokensByEmail[email] = token;

        // The app keeps its Matching/Item Service bearer token ONLY in memory (lib/apiClient.ts),
        // repopulated by AuthContext's GET /api/auth/me on every fresh mount. Every
        // Driver.Navigate().GoToUrl(...) below is a hard navigation, which wipes that in-memory
        // token; it only comes back if that /me call succeeds. Same cross-port cookie-delivery gap
        // ReportLostItemFixture.SyncAuthCookieToItemService already works around for ItemService -
        // here it blocks AuthService's own /me instead, which is what actually re-arms every other
        // service's calls, so every login (real or cached) needs the same fix.
        SyncAuthCookieToAuthService(token);
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
                // React can replace the button after a field-validation rerender.
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

        // CHAR(36) UUID columns come back as System.Guid from MySqlConnector, not string.
        return (Guid)result;
    }

    /// <summary>The id of the most recently created report owned by this user, of the given type.
    /// Used right after CreateLostReport/CreateFoundReport since the real wizard's success page
    /// never shows the new report's id.</summary>
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

    /// <summary>
    /// Waits for the real pipeline's row (created by the real Kafka event off the photo upload above)
    /// to exist, then patches it directly to the requested state. COMPLETED is unconditionally safe -
    /// ImageDescriptionWorker's ClaimNextAsync never selects a COMPLETED row. A parked PENDING row
    /// (attempts pre-set to MaxAttempts, 5) is also safe even under a race with the live worker: if the
    /// worker's own FailAsync lands after this update, its own WHERE clause requires
    /// processing_status = 'PROCESSING' to write anything back, which this update has already moved
    /// away from, so the worker's write silently affects zero rows and this parked state sticks.
    /// </summary>
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
}
