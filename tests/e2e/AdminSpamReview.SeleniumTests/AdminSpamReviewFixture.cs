using MySqlConnector;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;

namespace AdminSpamReview.SeleniumTests;

// Shared fixture for Story LF-87's Spam Review admin UI. One Chrome session logged in as a seeded
// admin, plus the seeding wiring this screen needs - the admin surface is about reviewing
// spam_records rows that already exist, and the only reliable way to get a row into a specific
// status (or with a flagged user whose account is gone) is to write the DB directly.
//
// Requires the local stack the page talks to: frontend on 5173 (VITE_ADMIN_API_BASE_URL pointing
// at AdminVerify on 5019), AuthService on 5261 (login + /api/admin/users contact lookups), and
// MySQL on 3306 (admin_verify_db + auth_service cross-database on one connection).
public sealed class AdminSpamReviewFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";

    public const string AdminEmail = "admin1@lostandfound.com";
    public const string AdminPassword = "Admin123!";

    // Same local dev MySQL the other Selenium suites use, reached cross-database on one connection.
    private const string ConnectionString =
        "Server=localhost;Port=3306;Uid=root;Pwd=yourpassword;AllowUserVariables=true;";

    public IWebDriver Driver { get; }
    public WebDriverWait Wait { get; }

    private bool _adminSessionEstablished;

    public AdminSpamReviewFixture()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-features=PasswordLeakDetection");

        Driver = new ChromeDriver(options);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }

    // ---- Admin session (part of the flow under test, so the real login form) -------------------

    // Idempotent on purpose. Auth Service rate-limits login to 10 requests per 5-minute window
    // (Program.cs), and this class would otherwise spend one per test and trip the limiter
    // mid-run - which fails every later test on a login timeout rather than on anything real.
    public void EnsureAdminSession()
    {
        if (_adminSessionEstablished)
        {
            return;
        }

        Driver.Navigate().GoToUrl(BaseUrl);
        Driver.Manage().Cookies.DeleteAllCookies();
        ((IJavaScriptExecutor)Driver).ExecuteScript(
            "window.localStorage.clear(); window.sessionStorage.clear();");

        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        Wait.Until(d => d.FindElement(By.Id("email"))).SendKeys(AdminEmail);
        Driver.FindElement(By.Id("password")).SendKeys(AdminPassword);
        Driver.FindElement(By.CssSelector("form button[type='submit']")).Click();

        Wait.Until(d => d.Url.Contains("/admin", StringComparison.Ordinal));

        _adminSessionEstablished = true;
    }

    // ---- Seeded spam_records rows --------------------------------------------------------------

    // Every Selenium-spawned auth user uses this email stamp, so a rerun can identify and remove
    // exactly the QA artifacts (rows + accounts) without touching real dev data. The account only
    // has to exist for Auth Service's contact endpoint to answer 200; nobody logs in as it, so the
    // password hash is never used.
    public Guid CreateThrowawayUser(string name)
    {
        var id = Guid.NewGuid();
        var email = $"selenium.sr.{Guid.NewGuid():N}@example.com";
        var phone = $"+94{Random.Shared.Next(100000000, 999999999)}";

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO auth_service.users (
                id, email, password_hash, name, phone_no, is_email_verified, created_at, updated_at
            ) VALUES (
                @id, @email, 'throwaway', @name, @phone, 1, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@phone", phone);

        command.ExecuteNonQuery();
        return id;
    }

    // Mirrors Auth Service's SoftDeleteAsync: sets deleted_at and frees the unique email/phone.
    // From the frontend's point of view the account is gone - GET /api/admin/users/{id} answers
    // 404, which drives the "Deleted user" cell on the Spam Review page.
    public void SoftDeleteUser(Guid userId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            UPDATE auth_service.users
            SET deleted_at = UTC_TIMESTAMP(3),
                email = CONCAT('del-', LEFT(@id, 8), '@deleted.local'),
                phone_no = CONCAT('DEL-', LEFT(@id, 8))
            WHERE id = @id;
            """,
            connection);

        command.Parameters.AddWithValue("@id", userId);
        command.ExecuteNonQuery();
    }

    public Guid SeedSpamRecord(Guid userId, int scoreA, string status, DateTime createdAtUtc)
    {
        var id = Guid.NewGuid();

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        // Columns mirror the Step 9 API-test seed shape (collecting_user_id NULL). Datetimes are
        // passed as pre-formatted strings so the value lands in the DB exactly as given - the
        // repository reads created_at back and stamps it DateTimeKind.Utc, so a midday-UTC literal
        // is safely inside the same UTC day for the from/to filter window.
        using var command = new MySqlCommand(
            """
            INSERT INTO admin_verify_db.spam_records (
                id, user_id, collecting_user_id, score_a, status,
                collecting_until, created_at, updated_at
            ) VALUES (
                @id, @userId, NULL, @scoreA, @status,
                @until, @createdAt, @createdAt
            );
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@scoreA", scoreA);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@until", Format(createdAtUtc.AddDays(7)));
        command.Parameters.AddWithValue("@createdAt", Format(createdAtUtc));

        command.ExecuteNonQuery();
        return id;
    }

    // Called at the start of every test: removes every spam_records row that belongs to QA
    // throwaway work - users stamped selenium.sr.*, soft-deleted accounts (del-*/deleted.local),
    // hard-deleted accounts, or ids that never existed - while leaving rows belonging to real
    // users untouched. This makes a rerun deterministically resume from the same empty active
    // queue even if a previous run died mid-test and skipped its own cleanup.
    public void PurgeSeleniumSpamRows()
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            DELETE FROM admin_verify_db.spam_records
            WHERE user_id NOT IN (
                SELECT id FROM auth_service.users
                WHERE email NOT LIKE 'selenium.sr.%@example.com'
                  AND email NOT LIKE 'del-%@deleted.local'
                  AND deleted_at IS NULL
            );
            DELETE FROM auth_service.users WHERE email LIKE 'selenium.sr.%@example.com';
            """,
            connection);

        command.ExecuteNonQuery();
    }

    // Removes exactly what one test seeded (opposite order not required - nothing in auth_service
    // references admin_verify_db, and spam_record_listings cascades off spam_records).
    public void DeleteSeeded(Guid[] recordIds, Guid[] userIds)
    {
        DeleteByIds(recordIds, "admin_verify_db.spam_records", "id");
        DeleteByIds(userIds, "auth_service.users", "id");
    }

    private void DeleteByIds(Guid[] ids, string table, string column)
    {
        if (ids.Length == 0) return;

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        // MySqlConnector disallows two parameters sharing a name (unlike SqlClient), so each id
        // gets its own @id{i} slot.
        using var command = new MySqlCommand(
            $"DELETE FROM {table} WHERE {column} IN (" +
            string.Join(",", ids.Select((_, index) => $"@id{index}")) + ");",
            connection);

        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue($"@id{index}", ids[index]);
        }

        command.ExecuteNonQuery();
    }

    // ---- Navigation ----------------------------------------------------------------------------

    public void NavigateToSpamReview()
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/admin/spam-review");
        Wait.Until(d => d.FindElement(By.XPath("//h1[normalize-space()='Spam Review']")));
    }

    private static string Format(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff");
}