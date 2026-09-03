using System.Net.Http.Json;

namespace AuthService.Tests.Integration;

public static class CookieTestHelpers
{
    /// <summary>
    /// Pulls the value of a named cookie out of a response's Set-Cookie header(s).
    /// Returns null if the cookie wasn't set on this response.
    /// </summary>
    public static string? ExtractCookieValue(this HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            var firstSegment = cookie.Split(';', 2)[0]; // "auth_token=xyz; HttpOnly; Path=/..."
            var parts = firstSegment.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == cookieName)
            {
                return parts[1].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Builds a request that sends the given cookie, since HttpClient from
    /// WebApplicationFactory has no cookie jar of its own between requests.
    /// </summary>
    public static HttpRequestMessage WithCookie(this HttpRequestMessage request, string cookieName, string cookieValue)
    {
        request.Headers.Add("Cookie", $"{cookieName}={cookieValue}");
        return request;
    }

    public static HttpRequestMessage NewJsonRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }
}
