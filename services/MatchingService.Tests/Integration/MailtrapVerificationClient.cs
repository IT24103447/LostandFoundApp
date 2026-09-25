using System.Net.Http.Headers;
using System.Text.Json;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Small Mailtrap REST client for MatchingService.Tests, mirroring
/// tests/e2e/VerifyEmailForm.SeleniumTests/MailtrapClient.cs (a separate project/assembly, so this is a
/// deliberate duplicate rather than a shared reference). Reads a real captured email's subject and body
/// via Mailtrap's own API, rather than extracting an OTP - this project needs to verify notification
/// content (the right subject per type, the right matched-items link), not fill in a form field.
///
/// Needs the same three environment variables as the Selenium client: MAILTRAP_API_TOKEN,
/// MAILTRAP_ACCOUNT_ID, MAILTRAP_INBOX_ID. If any are missing, IsConfigured returns false and the
/// Mailtrap-dependent test self-skips (see MatchNotificationMailtrapIntegrationTests) rather than
/// failing - this is a local-only, real-SMTP check, deliberately never wired into CI (see
/// JMeterTesting.md/session history: real third-party network calls stay local-only in this project).
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
