using AuthService.Models;

namespace AuthService.Repositories;

public interface IPasswordResetTokensRepository
{
    Task CreateAsync(PasswordResetToken token, CancellationToken ct = default);
    Task<PasswordResetToken?> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default);
    Task IncrementAttemptsAsync(Guid tokenId, CancellationToken ct = default);
    Task MarkUsedAsync(Guid tokenId, CancellationToken ct = default);
    Task InvalidateAllForUserAsync(Guid userId, CancellationToken ct = default);
    Task DeleteForUserAsync(Guid userId, CancellationToken ct = default);
}
