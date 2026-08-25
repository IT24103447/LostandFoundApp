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
    Task DeleteForUserAsync(Guid userId, CancellationToken ct = default);
}
