using System.Net.Http.Headers;
using System.Text.Json;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Small Mailtrap REST client reading a real captured email's subject/body, a deliberate duplicate
/// of tests/e2e/VerifyEmailForm.SeleniumTests/MailtrapClient.cs (separate assembly). If
/// MAILTRAP_API_TOKEN/ACCOUNT_ID/INBOX_ID are missing, IsConfigured is false and the dependent test self-skips rather than failing.
/// </summary>
public sealed class MailtrapVerificationClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://mailtrap.io/") };

    private readonly string _apiToken = Environment.GetEnvironmentVariable("MAILTRAP_API_TOKEN") ?? "";
    private readonly string _accountId = Environment.GetEnvironmentVariable("MAILTRAP_ACCOUNT_ID") ?? "";
    private readonly string _inboxId = Environment.GetEnvironmentVariable("MAILTRAP_INBOX_ID") ?? "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_apiToken) &&
        !string.IsNullOrWhiteSpace(_accountId) &&
        !string.IsNullOrWhiteSpace(_inboxId);

    /// <summary>Polls the inbox until it finds a message sent to <paramref name="recipientEmail"/>,
    /// then returns its subject and plain-text body. Throws if nothing shows up in time - callers
    /// should only call this after confirming <see cref="IsConfigured"/>.</summary>
    public async Task<(string Subject, string Body)> WaitForMessageAsync(
        string recipientEmail, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long? messageId = null;
        string? subject = null;

        while (DateTime.UtcNow < deadline)
        {
            (messageId, subject) = await FindLatestMessageAsync(recipientEmail);
            if (messageId is not null) break;
            await Task.Delay(1000);
        }

        if (messageId is null)
        {
            throw new TimeoutException(
                $"No email arrived for {recipientEmail} in Mailtrap inbox {_inboxId} within {timeout.TotalSeconds}s.");
        }

        var body = await GetMessageTextAsync(messageId.Value);
        return (subject!, body);
    }

    private async Task<(long? MessageId, string? Subject)> FindLatestMessageAsync(string recipientEmail)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/accounts/{_accountId}/inboxes/{_inboxId}/messages?search={Uri.EscapeDataString(recipientEmail)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return (null, null);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return (null, null);
        }

        foreach (var message in doc.RootElement.EnumerateArray())
        {
            var to = message.GetProperty("to_email").GetString() ?? "";
            if (to.Equals(recipientEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (message.GetProperty("id").GetInt64(), message.GetProperty("subject").GetString());
            }
        }

        return (null, null);
    }

    private async Task<string> GetMessageTextAsync(long messageId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/accounts/{_accountId}/inboxes/{_inboxId}/messages/{messageId}/body.txt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
