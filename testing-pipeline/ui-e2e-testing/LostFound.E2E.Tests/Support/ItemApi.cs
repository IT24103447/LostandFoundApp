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

    /// <summary>Creates a lost-item report owned by <paramref name="owner"/> and returns its title.</summary>
    public static string CreateLostItem(TestUser owner, string title, string category = "Accessories")
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
        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new InvalidOperationException(
                $"Could not create test item '{title}': {(int)response.StatusCode} {body}");
        }
        return title;
    }

    private static async Task<string> LoginAsync(TestUser user)
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
