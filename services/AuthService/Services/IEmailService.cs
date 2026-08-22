namespace AuthService.Services;

public interface IEmailService
{
    Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        string? plainTextBody = null,
        CancellationToken ct = default);
}
