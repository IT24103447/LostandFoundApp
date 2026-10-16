using System.Net.Http.Headers;
using System.Text.Json;
using MySqlConnector;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;

namespace AdminMatchAppeals.SeleniumTests;

// Shared fixture for Story LF-338. One Chrome session logged in as a seeded admin, plus the two
// kinds of test wiring this story needs:
//
//   1. Seeded appeal rows. The admin surface is about reviewing rows that already exist, and the
//      only way to get a row into a specific status (or with a reporter whose account is gone) is
//      to write it directly. RejectAsync touches nothing but the appeal row, so rejected and
//      pending fixtures need no live reports.
//
//   2. Real reports through the Item Service API. OpenAsync and VerifyAsync both re-read the two
//      reports from Item Service and refuse (409) unless both are still ACTIVE, so those two tests
//      cannot be faked and get genuine reports instead.
//
// Requires the full local stack: frontend on 5173, AuthService on 5261, ItemService on 5001,
// MatchingService on 5179, and MySQL on 3306.
public sealed class AdminAppealsFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";

    public const string AdminEmail = "admin1@lostandfound.com";
    public const string AdminPassword = "Admin123!";

    public const string UserAEmail = "user1@example.com";
    public const string UserAPassword = "User123!";
    public const string UserBEmail = "user2@example.com";
    public const string UserBPassword = "User123!";

    private const string AuthApiBaseUrl = "http://localhost:5261";
    private const string ItemApiBaseUrl = "http://localhost:5001";

    // Same local dev MySQL the other Selenium suites use, reached cross-database on one connection.
    private const string ConnectionString =
        "Server=localhost;Port=3306;Uid=root;Pwd=yourpassword;AllowUserVariables=true;";

    public IWebDriver Driver { get; }
    public WebDriverWait Wait { get; }

    private bool _adminSessionEstablished;

    // Tokens are cached per user for the same rate-limit reason as EnsureAdminSession: the dev
    // tokens last 24 hours, so one login per reporter covers every report these tests create.
    private readonly Dictionary<string, string> _tokensByEmail = new(StringComparer.Ordinal);

    public AdminAppealsFixture()
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
    // None of these tests logs out or invalidates the admin token, so one shared session is
    // both safe and the only way to stay under the limit.
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

    // ---- Seeded appeal rows -------------------------------------------------------------------

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

    // Writes a match_appeals row directly. The item ids default to fresh GUIDs, which is what most
    // admin tests want: RejectAsync and ListAsync never re-read the reports, so the ids can point
    // at nothing at all. Pass real ids for the Open/Verify tests.
    public Guid SeedAppeal(
        Guid lostReporterId,
        Guid finderId,
        string status,
        string lostTitle,
        string foundTitle,
        string? note = null,
        string? rejectionReason = null,
        Guid? lostItemId = null,
        Guid? foundItemId = null)
    {
        var id = Guid.NewGuid();
        var lostId = lostItemId ?? Guid.NewGuid();
        var foundId = foundItemId ?? Guid.NewGuid();
        var decided = status == "PENDING" ? (DateTime?)null : DateTime.UtcNow;

        var lostSnapshot =
            $$"""{"id":"{{lostId}}","type":"LOST","title":"{{lostTitle}}","category":"Other","description":"Seeded lost report for a Selenium LF-338 test.","date":"2026-10-01","location":"Seeded location"}""";
        var foundSnapshot =
            $$"""{"id":"{{foundId}}","type":"FOUND","title":"{{foundTitle}}","category":"Other","description":"Seeded found report for a Selenium LF-338 test.","date":"2026-10-01","location":"Seeded location"}""";

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            INSERT INTO matching_service.match_appeals (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id,
                appellant_id, appellant_role, appellant_email, appellant_phone,
                score, score_breakdown, lost_snapshot, found_snapshot,
                note, status, decided_by, decided_at, rejection_reason, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId,
                @appellantId, 'LOST', @email, @phone,
                @score, @breakdown, @lostSnapshot, @foundSnapshot,
                @note, @status, @decidedBy, @decidedAt, @rejectionReason,
                UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """,
            connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostId);
        command.Parameters.AddWithValue("@foundItemId", foundId);
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@appellantId", lostReporterId);
        command.Parameters.AddWithValue("@email", $"{lostReporterId:N}@example.com");
        command.Parameters.AddWithValue("@phone", "+94770000000");
        command.Parameters.AddWithValue("@score", 41.20m);
        command.Parameters.AddWithValue(
            "@breakdown",
            """{"title":50,"category":100,"description":40,"imageDescription":0,"imageAttributes":0}""");
        command.Parameters.AddWithValue("@lostSnapshot", lostSnapshot);
        command.Parameters.AddWithValue("@foundSnapshot", foundSnapshot);
        command.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue(
            "@decidedBy",
            status == "PENDING" ? DBNull.Value : GetUserId(AdminEmail));
        command.Parameters.AddWithValue("@decidedAt", (object?)decided ?? DBNull.Value);
        command.Parameters.AddWithValue("@rejectionReason", (object?)rejectionReason ?? DBNull.Value);

        command.ExecuteNonQuery();
        return id;
    }

    // Deletes every appeal this fixture seeded, so a rerun starts from the same admin list.
    public void DeleteSeededAppeals(Guid[] ids)
    {
        if (ids.Length == 0) return;

        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "DELETE FROM matching_service.match_appeals WHERE id IN (" +
            string.Join(",", ids.Select(_ => "@id")) + ");",
            connection);

        foreach (var id in ids)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        command.ExecuteNonQuery();
    }

    // ---- Throwaway users for the deleted-mid-session test --------------------------------------
    //
    // The account must genuinely exist when the queue page loads (Verify enabled), and only be
    // deleted afterwards, so it is the click-time re-check that catches it rather than a disabled
    // state the page could have computed at load. No FK constraint anywhere references
    // auth_service.users, so these rows can be written and removed with plain SQL.

    // Registers the account by inserting the row directly (id, name, unique email/phone). The
    // password hash is never used - nobody logs in as this user; it only has to exist for the
    // admin contact endpoint to answer 200.
    public Guid CreateThrowawayUser(string name)
    {
        var id = Guid.NewGuid();
        var email = $"selenium.lf352.{Guid.NewGuid():N}@example.com";
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

    // Mirrors Auth Service's SoftDeleteAsync (the self-delete path behind DELETE /api/auth/me):
    // sets deleted_at and frees the unique email/phone. From the frontend's point of view the
    // account is gone - GET /api/admin/users/{id} answers 404 - which is the LF-352 scenario.
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

    // Hard-removes a throwaway user (including one already soft-deleted), so a rerun does not
    // accumulate dead accounts.
    public void DeleteUser(Guid userId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "DELETE FROM auth_service.users WHERE id = @id;", connection);

        command.Parameters.AddWithValue("@id", userId);
        command.ExecuteNonQuery();
    }

    // ---- Real reports, for the tests that cannot fake them -------------------------------------

    // Creates one genuine LOST and one FOUND report via the Item Service API. Used only where the
    // story re-reads both reports and insists they are still active; the reports themselves are
    // setup, not the behaviour under test, so the multi-step report wizard is not driven here.
    public (Guid LostItemId, Guid FoundItemId) CreateReportPairViaApi(
        string lostTitle,
        string foundTitle,
        string lostDescription,
        string foundDescription)
    {
        var date = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");

        var lostId = ReportItem(
            "lost",
            UserAEmail,
            UserAPassword,
            lostTitle,
            lostDescription,
            "LastKnownLocation",
            "Seeded Central Library",
            date);

        var foundId = ReportItem(
            "found",
            UserBEmail,
            UserBPassword,
            foundTitle,
            foundDescription,
            "LocationFound",
            "Seeded City Gym",
            date);

        return (lostId, foundId);
    }

    private Guid ReportItem(
        string kind,
        string email,
        string password,
        string title,
        string description,
        string locationField,
        string location,
        string date)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenFor(email, password));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(title), "Title");
        form.Add(new StringContent("Other"), "Category");
        form.Add(new StringContent(description), "Description");
        form.Add(new StringContent(location), locationField);
        form.Add(new StringContent("Seeded hidden information for a Selenium LF-338 test."), "HiddenInformation");
        form.Add(new StringContent(date), kind == "lost" ? "DateLost" : "DateFound");

        var response = client
            .PostAsync($"{ItemApiBaseUrl}/api/items/{kind}", form)
            .GetAwaiter()
            .GetResult();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Creating the {kind} report failed with {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private string TokenFor(string email, string password)
    {
        if (_tokensByEmail.TryGetValue(email, out var cached))
        {
            return cached;
        }

        using var client = new HttpClient();

        var payload = JsonSerializer.Serialize(new { email, password });
        var response = client
            .PostAsync(
                $"{AuthApiBaseUrl}/api/auth/login",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"))
            .GetAwaiter()
            .GetResult();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Login for {email} failed with {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var token = document.RootElement.GetProperty("token").GetString()!;
        _tokensByEmail[email] = token;
        return token;
    }

    // ---- Assertions the UI cannot make on its own ---------------------------------------------

    public string? GetAppealStatus(Guid appealId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT status FROM matching_service.match_appeals WHERE id = @id LIMIT 1;", connection);
        command.Parameters.AddWithValue("@id", appealId);

        return ReadNullableString(command.ExecuteScalar());
    }

    public string? GetRejectionReason(Guid appealId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            "SELECT rejection_reason FROM matching_service.match_appeals WHERE id = @id LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("@id", appealId);

        return ReadNullableString(command.ExecuteScalar());
    }

    // Verifying an appeal has to create the match, not just flip the status - this is the only
    // place that can be checked without trusting the admin screen's own wording.
    public string? GetMatchIdForPair(Guid lostItemId, Guid foundItemId)
    {
        using var connection = new MySqlConnection(ConnectionString);
        connection.Open();

        using var command = new MySqlCommand(
            """
            SELECT id FROM matching_service.matches
            WHERE lost_item_id = @lostItemId AND found_item_id = @foundItemId
            LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);

        return ReadNullableString(command.ExecuteScalar());
    }

    // Every id column in these schemas is char(36), and MySqlConnector hands a value that parses
    // as a UUID back as a Guid rather than a string. Reading through this keeps the callers from
    // having to care which of the two they got.
    private static string? ReadNullableString(object? value) => value switch
    {
        null or DBNull => null,
        Guid guid => guid.ToString(),
        _ => (string)value,
    };
}