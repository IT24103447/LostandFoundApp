using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResetPasswordForm.SeleniumTests;

/// <summary>
/// Tiny helper that reads the real OTP out of the Mailtrap sandbox inbox the backend
/// is configured to send to in Development (see appsettings.Development.json → Smtp).
///
/// This is what makes Scenario 1 (successful verification) and Scenario 6 (posting
/// restriction lifted) possible as TRUE end-to-end tests — without it, Selenium has no
/// way to know the 6-digit code the backend emailed, since it's never echoed back in
/// any API response (correctly — that would be a security hole).
///
/// Needs three environment variables, all from your Mailtrap account (Email Testing →
/// your inbox → "SMTP Settings" page has the inbox ID in the URL; the API token is
/// under Settings → API Tokens):
///
///   MAILTRAP_API_TOKEN
///   MAILTRAP_ACCOUNT_ID
///   MAILTRAP_INBOX_ID
///
/// If any of these are missing, <see cref="IsConfigured"/> returns false and the
/// Mailtrap-dependent tests skip themselves (see VerifyEmailFormTests.cs) rather than
/// failing — so the rest of the suite still runs fine without a Mailtrap account.
/// </summary>
public class MailtrapClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://mailtrap.io/") };

    private readonly string _apiToken = Environment.GetEnvironmentVariable("MAILTRAP_API_TOKEN") ?? "";
    private readonly string _accountId = Environment.GetEnvironmentVariable("MAILTRAP_ACCOUNT_ID") ?? "";
    private readonly string _inboxId = Environment.GetEnvironmentVariable("MAILTRAP_INBOX_ID") ?? "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_apiToken) &&
        !string.IsNullOrWhiteSpace(_accountId) &&
        !string.IsNullOrWhiteSpace(_inboxId);

    /// <summary>
    /// Polls the inbox until it finds a message sent to <paramref name="recipientEmail"/>,
    /// then extracts the 6-digit code from its body. Throws if nothing shows up in time —
    /// callers should only call this after confirming <see cref="IsConfigured"/>.
    /// </summary>
    public async Task<string> WaitForOtpAsync(string recipientEmail, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long? messageId = null;

        while (DateTime.UtcNow < deadline)
        {
            messageId = await FindLatestMessageIdAsync(recipientEmail);
            if (messageId is not null) break;
            await Task.Delay(1000);
        }

        if (messageId is null)
        {
            throw new TimeoutException(
                $"No verification email arrived for {recipientEmail} in Mailtrap inbox {_inboxId} within {timeout.TotalSeconds}s.");
        }

        var body = await GetMessageTextAsync(messageId.Value);
        var match = Regex.Match(body, @"\b\d{6}\b");
        if (!match.Success)
        {
            throw new InvalidOperationException("Verification email body did not contain a 6-digit code.");
        }

        return match.Value;
    }

    private async Task<long?> FindLatestMessageIdAsync(string recipientEmail)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/accounts/{_accountId}/inboxes/{_inboxId}/messages?search={Uri.EscapeDataString(recipientEmail)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);

        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        // Mailtrap returns messages newest-first; take the first match for this recipient.
        foreach (var message in doc.RootElement.EnumerateArray())
        {
            var to = message.GetProperty("to_email").GetString() ?? "";
            if (to.Equals(recipientEmail, StringComparison.OrdinalIgnoreCase))
            {
                return message.GetProperty("id").GetInt64();
            }
        }

        return null;
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
