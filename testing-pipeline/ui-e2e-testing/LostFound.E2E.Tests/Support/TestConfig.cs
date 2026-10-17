namespace LostFound.E2E.Tests.Support;

public sealed record TestUser(string Email, string Password);

/// <summary>
/// All environment-dependent values in one place. Defaults match the pipeline's seeded data
/// (users.csv / admin.csv); override with environment variables when needed.
/// </summary>
public static class TestConfig
{
    public static string BaseUrl => Env("E2E_BASE_URL", "http://localhost:5173").TrimEnd('/');

    // true in CI (no display); false for the live demo so the browser is visible.
    public static bool Headless =>
        string.Equals(Env("E2E_HEADLESS", "false"), "true", StringComparison.OrdinalIgnoreCase);

    public static string ScreenshotDir =>
        Env("E2E_SCREENSHOT_DIR", Path.Combine(AppContext.BaseDirectory, "screenshots"));

    public static TestUser User1 =>
        new(Env("E2E_USER1_EMAIL", "user1@example.com"), Env("E2E_USER1_PASSWORD", "User123!"));

    public static TestUser Admin =>
        new(Env("E2E_ADMIN_EMAIL", "admin1@lostandfound.com"), Env("E2E_ADMIN_PASSWORD", "Admin123!"));

    /// <summary>
    /// Seeded account reserved for kick tests. No other test logs in as this user (reporters are
    /// user2..user19 and the viewer is user1), so kicking it can never break another test.
    /// </summary>
    public static TestUser KickTarget =>
        new(Env("E2E_KICK_EMAIL", "user20@example.com"), Env("E2E_USERS_PASSWORD", "User123!"));

    // The back-end APIs are used only to ARRANGE test data quickly (see ItemApi). What a test
    // verifies always goes through the browser.
    public static string AuthApiUrl => Env("E2E_AUTH_API_URL", "http://localhost:5261").TrimEnd('/');
    public static string ItemApiUrl => Env("E2E_ITEM_API_URL", "http://localhost:5001").TrimEnd('/');

    /// <summary>
    /// A seeded user other than user1, chosen at random. Spreading created reports across
    /// user2..user19 keeps every user under the spam rule (more than 3 reports gets flagged).
    /// </summary>
    public static TestUser PickUser()
    {
        var n = Random.Shared.Next(2, 20);
        return new TestUser($"user{n}@example.com", Env("E2E_USERS_PASSWORD", "User123!"));
    }

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
}
