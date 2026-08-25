using AuthService.Models;

namespace AuthService.Repositories;

public interface IUsersRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<bool> PhoneExistsAsync(string phoneNo, CancellationToken ct = default);
    Task CreateAsync(User user, CancellationToken ct = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> IsEmailRegisteredAsync(string email, CancellationToken ct = default);
    Task<DateTime?> GetLastResentAtAsync(Guid userId, CancellationToken ct = default);
    Task SetLastResentAtAsync(Guid userId, DateTime at, CancellationToken ct = default);
    Task MarkEmailVerifiedAsync(Guid userId, CancellationToken ct = default);
    Task UpdateEmailAsync(Guid userId, string newEmail, CancellationToken ct = default);
    Task UpdateProfileAsync(Guid userId, string name, string phoneNo, CancellationToken ct = default);
    Task<bool> PhoneExistsForOtherUserAsync(Guid userId, string phoneNo, CancellationToken ct = default);
    Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default);


    /// <summary>Returns the most recent verification status for the given user</summary>
    Task<UserVerificationStatus> GetVerificationStatusAsync(Guid userId, CancellationToken ct = default);
}

public record UserVerificationStatus(
    bool IsEmailVerified);
