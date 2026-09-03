using System.Text.RegularExpressions;
using AuthService.Services;

namespace AuthService.Tests.Integration.Fakes;

public record SentEmail(string To, string Subject, string HtmlBody, string? PlainTextBody, int SequenceNumber);

public class FakeEmailService : IEmailService
{
    private readonly object _lock = new();
    private readonly List<SentEmail> _sent = new();
    private int _sequence;

    public Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sent.Add(new SentEmail(to, subject, htmlBody, plainTextBody, ++_sequence));
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<SentEmail> Sent
    {
        get { lock (_lock) { return _sent.ToList(); } }
    }

    public SentEmail? LastSentTo(string email)
    {
        lock (_lock)
        {
            return _sent
                .Where(e => string.Equals(e.To, email, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.SequenceNumber)
                .FirstOrDefault();
        }
    }

    public string GetLastOtpCodeFor(string email)
    {
        var last = LastSentTo(email)
            ?? throw new InvalidOperationException($"No email was sent to '{email}'.");
        var match = Regex.Match(last.HtmlBody, @"\b\d{6}\b");
        if (!match.Success)
            throw new InvalidOperationException($"Could not find a 6-digit code in the email sent to '{email}'.");
        return match.Value;
    }

    public void Clear()
    {
        lock (_lock) { _sent.Clear(); }
    }
}