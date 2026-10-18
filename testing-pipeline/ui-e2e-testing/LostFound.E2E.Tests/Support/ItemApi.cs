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

    public static string UniqueTitle(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    /// <summary>Creates a lost-item report for <paramref name="owner"/> and returns the title it was stored under.</summary>
    public static string CreateLostItem(TestUser owner, string title, string category = "Accessories") =>
        CreateLostItemWithId(owner, title, category).Title;

    /// <summary>
    /// Like <see cref="CreateLostItem"/>, but also returns the report's id, which is needed to build
    /// /items/:id and /edit-lost-item/:id URLs. If the connection is reset (a busy local stack does
    /// this), the request is retried under a slightly different title: the first attempt may already
    /// have been saved, and reusing the title could leave a duplicate that confuses delete and rename checks.
    /// </summary>
    public static (string Title, string Id) CreateLostItemWithId(TestUser owner, string title, string category = "Accessories")
    {
        for (var attempt = 1; ; attempt++)
        {
            var attemptTitle = attempt == 1 ? title : $"{title}-r{attempt}";
            try
            {
                return CreateLostItemOnce(owner, attemptTitle, category);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                Thread.Sleep(TimeSpan.FromSeconds(3 * attempt));
            }
        }
    }

    private static (string Title, string Id) CreateLostItemOnce(TestUser owner, string title, string category)
    {
        var token = LoginAsync(owner).GetAwaiter().GetResult();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(title), "Title" },
            { new StringContent(category), "Category" },
            { new StringContent($"E2E test report: {title}"), "Description" },
            { new StringContent(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd")), "DateLost" },
            { new StringContent("Main campus library"), "LastKnownLocation" },
            { new StringContent($"hidden-{Guid.NewGuid():N}"), "HiddenInformation" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConfig.ItemApiUrl}/api/items/lost")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = Http.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not create test item '{title}': {(int)response.StatusCode} {body}");
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
