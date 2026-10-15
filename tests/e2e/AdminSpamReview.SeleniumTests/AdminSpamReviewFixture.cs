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
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(30));
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

    // Removes the case-action test's non-record rows: the tracked_listings row a record's links
    // point at, and the ItemService lost item the Solve runner deletes through the admin API
    // (its photos cascade off lost_items).
    public void DeleteSeededItems(params Guid[] listingIds)
    {
        DeleteByIds(listingIds, "admin_verify_db.tracked_listings", "listing_id");
        DeleteByIds(listingIds, "item_service.lost_items", "id");
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

    // ---- Seeded case-action rows (LF-267/268/269) -----------------------------------------------

    // The purge for the case-action tests. PurgeSeleniumSpamRows only touches spam_records +
    // their auth accounts; the case flow also writes tracked_listings and ItemService items, so a
    // rerun needs those gone too (a solve test that dies mid-run leaves its item soft-deleted).
    // Everything that belongs to QA throwaway work is removed; real users' rows are untouched.
    public void PurgeSeleniumCaseData()
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        // Order matters for the FKs: spam_records first so their spam_record_listings cascade
        // away before tracked_listings rows are deleted, and ItemService photos cascade off the
        // deleted lost_items rows.
        using var command = new MySqlCommand(
            """
            DELETE FROM admin_verify_db.spam_records
            WHERE user_id NOT IN (
                SELECT id FROM auth_service.users
                WHERE email NOT LIKE 'selenium.sr.%@example.com'
                  AND email NOT LIKE 'del-%@deleted.local'
                  AND deleted_at IS NULL
            );
            DELETE FROM admin_verify_db.tracked_listings
            WHERE user_id NOT IN (
                SELECT id FROM auth_service.users
                WHERE email NOT LIKE 'selenium.sr.%@example.com'
                  AND email NOT LIKE 'del-%@deleted.local'
                  AND deleted_at IS NULL
            );
            DELETE FROM item_service.lost_items
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

    // A tracked_listings row - the table spam_record_listings links out to. Same throwaway user
    // as the record, so the flagging is coherent.
    public Guid SeedTrackedListing(Guid userId, DateTime postedAtUtc, string listingType = "LOST")
    {
        var listingId = Guid.NewGuid();

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO admin_verify_db.tracked_listings (
                listing_id, listing_type, user_id, posted_at, created_at
            ) VALUES (
                @id, @listingType, @userId, @postedAt, @postedAt
            );
            """,
            connection);

        command.Parameters.AddWithValue("@id", listingId);
        command.Parameters.AddWithValue("@listingType", listingType);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@postedAt", Format(postedAtUtc));

        command.ExecuteNonQuery();
        return listingId;
    }

    // Links a tracked listing to a spam record. solveResult seeds the mid-solve state a resume
    // test needs (DELETED/SKIPPED/FAILED) or stays NULL for a listing Solve still has to handle.
    public void SeedLinkedList(Guid recordId, Guid listingId, DateTime addedAtUtc, string? solveResult = null)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO admin_verify_db.spam_record_listings (
                spam_record_id, listing_id, solve_result, added_at
            ) VALUES (
                @recordId, @listingId, @solveResult, @addedAt
            );
            """,
            connection);

        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@solveResult", (object?)solveResult ?? DBNull.Value);
        command.Parameters.AddWithValue("@addedAt", Format(addedAtUtc));

        command.ExecuteNonQuery();
    }

    // An ACTIVE ItemService lost item for a listing plus one photo row, so the popup renders the
    // real title, an Active badge and a photo strip - and the Solve runner's admin DELETE has a
    // row to actually soft-delete. id doubles as the listingId (the item channel keys admin item
    // calls by the listing id).
    public void SeedLostItem(Guid listingId, Guid userId, string title)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO item_service.lost_items (
                id, user_id, title, category, description, date_lost,
                last_known_location, hidden_information, status, created_at, updated_at
            ) VALUES (
                @id, @userId, @title, 'Electronics', @description, CURRENT_DATE,
                'Unknown', 'qa-seed', 'ACTIVE', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            INSERT INTO item_service.lost_item_photos (id, lost_item_id, url, created_at)
            VALUES (UUID(), @id, @photoUrl, UTC_TIMESTAMP(3));
            """,
            connection);

        command.Parameters.AddWithValue("@id", listingId);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@description", "Selenium-seeded lost item used by the case-action flow tests.");
        command.Parameters.AddWithValue("@photoUrl", "/photos/selenium-seed.png");

        command.ExecuteNonQuery();
    }

    // ---- Case-action DB reads -------------------------------------------------------------------

    public string ReadSpamStatus(Guid recordId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT status FROM admin_verify_db.spam_records WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", recordId);

        return (string)command.ExecuteScalar()!;
    }

    public string ReadKickStatus(Guid recordId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT kick_status FROM admin_verify_db.spam_records WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", recordId);

        return (string)command.ExecuteScalar()!;
    }

    public string? ReadListingSolveResult(Guid recordId, Guid listingId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        // null when the row is still unsolved, "DELETED"/"SKIPPED"/"FAILED" once solved.
        using var command = new MySqlCommand(
            """
            SELECT solve_result FROM admin_verify_db.spam_record_listings
            WHERE spam_record_id = @recordId AND listing_id = @listingId;
            """,
            connection);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@listingId", listingId);

        var value = command.ExecuteScalar();
        return value as string;
    }

    // true = soft-deleted (deleted_at set), false = still active, null = no such item.
    public bool? ReadItemDeletedAt(Guid listingId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT deleted_at FROM item_service.lost_items WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", listingId);

        var value = command.ExecuteScalar();
        return value is null ? null : value is DBNull ? false : true;
    }

    // Whether Auth Service soft-banned the user (the Solve runner's kick step sets this).
    public bool ReadUserKicked(Guid userId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT is_kicked FROM auth_service.users WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", userId);

        return Convert.ToInt32(command.ExecuteScalar()) == 1;
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