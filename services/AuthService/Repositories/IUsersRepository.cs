using AuthService.Models;

namespace AuthService.Repositories;

public interface IUsersRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<bool> PhoneExistsAsync(string phoneNo, CancellationToken ct = default);
    Task CreateAsync(User user, CancellationToken ct = default);
}
