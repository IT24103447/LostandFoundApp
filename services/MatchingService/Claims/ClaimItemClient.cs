using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MatchingService.Claims;

public sealed class ClaimItemClient
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _contextAccessor;

    public ClaimItemClient(
        HttpClient http,
        IHttpContextAccessor contextAccessor)
    {
        _http = http;
        _contextAccessor = contextAccessor;
    }

    public async Task<ItemReport> GetAsync(
        string type,
        Guid id,
        CancellationToken cancellationToken)
    {
        var segment = GetSegment(type);

        var report = await GetJsonAsync<ItemReport>(
            $"api/items/{segment}/{id}",
            cancellationToken);

        if (report.Id != id || report.UserId == Guid.Empty)
        {
            throw new ClaimException(
                StatusCodes.Status503ServiceUnavailable,
                "Item details could not be verified. Please try again.");
        }

        return report;
    }

    public async Task<List<ClaimItemView>> GetMineAsync(
        string type,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var normalizedType = type.Trim().ToUpperInvariant();
        var segment = GetSegment(normalizedType);

        var reports = await GetJsonAsync<List<ItemReport>>(
            $"api/items/{segment}/mine",
            cancellationToken);

        return reports
            .Where(report =>
                report.UserId == userId &&
                string.Equals(
                    report.Status,
                    "ACTIVE",
                    StringComparison.OrdinalIgnoreCase))
            .Select(report => report.ToView(normalizedType))
            .ToList();
    }

    private async Task<T> GetJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        var context = _contextAccessor.HttpContext
            ?? throw new ClaimException(
                StatusCodes.Status401Unauthorized,
                "Please sign in.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            path);

        var authorization =
            context.Request.Headers.Authorization.ToString();

        if (AuthenticationHeaderValue.TryParse(
                authorization,
                out var header) &&
            string.Equals(
                header.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = header;
        }
        else if (context.Request.Cookies.TryGetValue(
                     "auth_token",
                     out var token))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            throw new ClaimException(
                StatusCodes.Status401Unauthorized,
                "Please sign in.");
        }

        try
        {
            using var response = await _http.SendAsync(
                request,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ClaimException(
                    StatusCodes.Status401Unauthorized,
                    "Please sign in again.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ClaimException(
                    StatusCodes.Status403Forbidden,
                    "You do not have permission to access this report.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ClaimException(
                    StatusCodes.Status404NotFound,
                    "One of the reports is no longer available.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaimException(
                    StatusCodes.Status503ServiceUnavailable,
                    "Item Service is unavailable. Please try again.");
            }

            return await response.Content.ReadFromJsonAsync<T>(
                       cancellationToken: cancellationToken)
                   ?? throw new ClaimException(
                       StatusCodes.Status503ServiceUnavailable,
                       "Item Service returned an invalid response.");
        }
        catch (HttpRequestException)
        {
            throw new ClaimException(
                StatusCodes.Status503ServiceUnavailable,
                "Item Service is unavailable. Please try again.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaimException(
                StatusCodes.Status503ServiceUnavailable,
                "Item Service took too long to respond.");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ClaimException(
                StatusCodes.Status503ServiceUnavailable,
                "Item Service returned an invalid response.");
        }
    }

    private static string GetSegment(string type)
    {
        return type switch
        {
            "LOST" => "lost",
            "FOUND" => "found",
            _ => throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Report type must be LOST or FOUND.")
        };
    }
}