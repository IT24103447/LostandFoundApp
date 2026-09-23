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

    /// <summary>The id of the most recently created match involving this user, on either side. Used
    /// right after a real claim submission through ClaimDialog, whose own success view never shows
    /// the new match's id.</summary>
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

    /// <summary>
    /// Inserts a "matches" row directly for statuses/flags no real endpoint can currently produce
    /// (CONFIRMED, REJECTED, AUTO_REJECTED_LOW_CONFIDENCE, or a deactivated row): there is no
    /// Confirm/Reject action anywhere in the product yet, and nothing deactivates a match on demand.
    /// Item ids are random rather than real reports, which is safe here: the Matches list and its
    /// section grouping read only the stored lost/found snapshot JSON, never re-fetching the source
    /// report, and the review screen's own live photo lookup (MatchItemCard) fails closed to "Photo
    /// unavailable" rather than breaking the page when an id doesn't resolve.
    /// </summary>
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

    private (Guid UserId, string Email, string Password)? _cachedFreshUser;

    /// <summary>
    /// Registers one new account, the first time any test asks for it, and hands out that same
    /// account to every later caller in this run. AuthService rate-limits /api/auth/register (its own
    /// fixed-window "register" limiter - AuthService/Program.cs), the same global-bucket shape as the
    /// login limiter Story 2's tests already had to work around, so registering a fresh account per
    /// test would burn through it fast across repeated runs. Every current caller only needs "some
    /// account that isn't the two long-lived seeded ones" - none needs its own exclusive registration.
    /// </summary>
    public (Guid UserId, string Email, string Password) GetOrRegisterFreshVerifiedUser()
    {
        return _cachedFreshUser ??= RegisterFreshVerifiedUser();
    }

    /// <summary>
    /// Registers a new account through the real /register form, then marks it email-verified
    /// directly (skipping the real Mailtrap send/click, which VerifyEmailForm.SeleniumTests already
    /// covers on its own) so it can log in immediately. Used only for the one Story 3 scenario that
    /// needs a guaranteed, never-before-seen zero-matches account: the two shared seed accounts
    /// (UserAEmail/UserBEmail) accumulate real matches across every earlier test and every earlier
    /// run of this suite, so neither can reliably stand in for "a user with no matches at all". Call
    /// GetOrRegisterFreshVerifiedUser() instead of this directly, to avoid the register rate limiter.
    /// </summary>
    private (Guid UserId, string Email, string Password) RegisterFreshVerifiedUser()
    {
        var email = $"selenium.story3.{Guid.NewGuid():N}@example.com";
        const string password = "Str0ngPass1";
        var digits = Random.Shared.Next(100000000, 999999999);

        // A leftover auth_token cookie from an earlier test in this run would redirect away from the
        // register form (the same reason LoginAs clears cookies first). That redirect can also land
        // mid-fill rather than only on the first navigation, so the whole fill-and-submit sequence is
        // retried as one atomic block, not just the first field.
        Driver.Navigate().GoToUrl(BaseUrl);
        Driver.Manage().Cookies.DeleteAllCookies();
        Driver.Navigate().GoToUrl($"{BaseUrl}/register");

        try
        {
            Wait.Until(d =>
            {
                try
                {
                    // Clear() before SendKeys() so a retried attempt (elements already filled by a
                    // prior, failed attempt) overwrites rather than appends.
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
