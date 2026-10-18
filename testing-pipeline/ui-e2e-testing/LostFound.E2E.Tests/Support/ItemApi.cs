using System.Net.Http.Headers;
using System.Text.Json;

namespace LostFound.E2E.Tests.Support;

/// <summary>
/// Creates test data through the Auth and Item APIs so a browser test does not have to click
/// through a whole wizard just to get something to search for. Used for ARRANGE only.
/// </summary>
public static class ItemApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private enum Kind { Lost, Found }

    public static string UniqueTitle(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    /// <summary>Creates a lost-item report for <paramref name="owner"/> and returns the title it was stored under.</summary>
    public static string CreateLostItem(
        TestUser owner, string title, string category = "Accessories", string? description = null) =>
        CreateLostItemWithId(owner, title, category, description: description).Title;

    /// <summary>
    /// Like <see cref="CreateLostItem"/>, but also returns the report's id, which is needed to build
    /// /items/:id and /edit-lost-item/:id URLs. If the connection is reset (a busy local stack does
    /// this), the request is retried under a slightly different title: the first attempt may already
    /// have been saved, and reusing the title could leave a duplicate that confuses delete and rename checks.
    /// </summary>
    public static (string Title, string Id) CreateLostItemWithId(
        TestUser owner, string title, string category = "Accessories", bool withPhoto = false, string? description = null) =>
        CreateWithRetry(owner, Kind.Lost, title, category, withPhoto, description);

    /// <summary>Creates a found-item report (same retry rules as <see cref="CreateLostItemWithId"/>).</summary>
    public static (string Title, string Id) CreateFoundItemWithId(
        TestUser owner, string title, string category = "Accessories", bool withPhoto = false, string? description = null) =>
        CreateWithRetry(owner, Kind.Found, title, category, withPhoto, description);

    private static (string Title, string Id) CreateWithRetry(
        TestUser owner, Kind kind, string title, string category, bool withPhoto, string? description)
    {
        for (var attempt = 1; ; attempt++)
        {
            var attemptTitle = attempt == 1 ? title : $"{title}-r{attempt}";
            try
            {
                return CreateOnce(owner, kind, attemptTitle, category, withPhoto, description);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                // Retry back-off in an ARRANGE helper (not a UI wait): lets a briefly overloaded API recover.
                Task.Delay(TimeSpan.FromSeconds(3 * attempt)).GetAwaiter().GetResult();
            }
        }
    }

    private static (string Title, string Id) CreateOnce(
        TestUser owner, Kind kind, string title, string category, bool withPhoto, string? description)
    {
        var token = LoginAsync(owner).GetAwaiter().GetResult();
        var date = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(title), "Title" },
            { new StringContent(category), "Category" },
            // By default the description repeats the title; pass a description when a test renames the report
            // (search also matches descriptions, so the old title would otherwise still be found).
            { new StringContent(description ?? $"E2E test report: {title}"), "Description" },
            { new StringContent($"hidden-{Guid.NewGuid():N}"), "HiddenInformation" },
        };

        if (kind == Kind.Lost)
        {
            form.Add(new StringContent(date), "DateLost");
            form.Add(new StringContent("Main campus library"), "LastKnownLocation");
        }
        else
        {
            form.Add(new StringContent(date), "DateFound");
            form.Add(new StringContent("Main campus library"), "LocationFound");
        }

        if (withPhoto)
        {
            var photo = new ByteArrayContent(File.ReadAllBytes(TestFiles.ValidImage()));
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(photo, "Photos", "e2e-photo.png");
        }

        var url = kind == Kind.Lost ? $"{TestConfig.ItemApiUrl}/api/items/lost" : $"{TestConfig.ItemApiUrl}/api/items/found";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = Http.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not create test {kind.ToString().ToLowerInvariant()} item '{title}': {(int)response.StatusCode} {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var id) && !root.TryGetProperty("Id", out id))
            throw new InvalidOperationException("The create response contained no id.");
        return (title, id.GetString()!);
    }

    /// <summary>Login is idempotent, so a transient network reset is retried a few times.</summary>
    private static async Task<string> LoginAsync(TestUser user)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await LoginOnceAsync(user);
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
    }

    private static async Task<string> LoginOnceAsync(TestUser user)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { email = user.Email, password = user.Password }),
            System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"{TestConfig.AuthApiUrl}/api/auth/login", content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API login failed for {user.Email}: {(int)response.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("token").GetString()
               ?? throw new InvalidOperationException("Login response had no token.");
    }
}
