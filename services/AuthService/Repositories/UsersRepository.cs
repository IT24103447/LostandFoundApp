using AuthService.Databases;
using AuthService.Models;
using MySqlConnector;

namespace AuthService.Repositories;

public class UsersRepository : IUsersRepository
{
    private readonly IDbConnectionFactory _db;

    public UsersRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE email = @email;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@email", email);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task<bool> PhoneExistsAsync(string phoneNo, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE phone_no = @phone;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@phone", phoneNo);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task CreateAsync(User user, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO users
                (id, email, password_hash, name, phone_no, is_admin, is_email_verified)
            VALUES
                (@id, @email, @passwordHash, @name, @phoneNo, @isAdmin, @isEmailVerified);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", user.Id.ToString());
        cmd.Parameters.AddWithValue("@email", user.Email);
        cmd.Parameters.AddWithValue("@passwordHash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@name", user.Name);
        cmd.Parameters.AddWithValue("@phoneNo", user.PhoneNo);
        cmd.Parameters.AddWithValue("@isAdmin", user.IsAdmin ? 1 : 0);
        cmd.Parameters.AddWithValue("@isEmailVerified", user.IsEmailVerified ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
