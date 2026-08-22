using AuthService.Models;

namespace AuthService.Repositories;

public interface IEmailVerificationTokensRepository
{
    Task CreateAsync(
        Guid userId,
        string codeHash,
        string? pendingEmail,
        DateTime expiresAt,
        CancellationToken ct = default);

    Task<EmailVerificationToken?> GetActiveByHashAsync(string codeHash, CancellationToken ct = default);

    Task<EmailVerificationToken?> GetActiveByUserAsync(Guid userId, CancellationToken ct = default);

    Task IncrementAttemptsAsync(Guid tokenId, CancellationToken ct = default);

    Task MarkUsedAsync(Guid tokenId, CancellationToken ct = default);

    Task InvalidateAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Marks the most recent active token for the given email as bounced (without marking used,
    /// so it can't be reused). Looks up by either the user_id (when a user record exists for the email)
    /// or by the pending_email target. Returns the number of rows affected.
    ///</summary>
    Task<int> MarkLatestBouncedForEmailAsync(string email, CancellationToken ct = default);
}
