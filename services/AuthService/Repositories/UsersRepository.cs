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
                (id, email, password_hash, name, phone_no, is_admin, is_email_verified, is_kicked)
            VALUES
                (@id, @email, @passwordHash, @name, @phoneNo, @isAdmin, @isEmailVerified, @isKicked);
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
        cmd.Parameters.AddWithValue("@isKicked", user.IsKicked ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, email, password_hash, name, phone_no, is_admin, is_email_verified, is_kicked, created_at, updated_at
            FROM users WHERE email = @email LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@email", email);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, email, password_hash, name, phone_no, is_admin, is_email_verified, is_kicked, created_at, updated_at
            FROM users WHERE id = @id LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<bool> IsEmailRegisteredAsync(string email, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE email = @email;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@email", email);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task<DateTime?> GetLastResentAtAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT last_resent_at FROM users WHERE id = @id;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == DBNull.Value || result == null ? null : Convert.ToDateTime(result);
    }

    public async Task SetLastResentAtAsync(Guid userId, DateTime at, CancellationToken ct = default)
    {
        const string sql = "UPDATE users SET last_resent_at = @at WHERE id = @id;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@at", at);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkEmailVerifiedAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET is_email_verified = 1, updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateEmailAsync(Guid userId, string newEmail, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET email = @email, updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@email", newEmail);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task UpdateProfileAsync(Guid userId, string name, string phoneNo, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET name = @name, phone_no = @phoneNo, updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@phoneNo", phoneNo);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> PhoneExistsForOtherUserAsync(Guid userId, string phoneNo, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE phone_no = @phone AND id <> @id;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@phone", phoneNo);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public async Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET password_hash = @hash, updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@hash", newHash);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM users WHERE id = @id;";
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UserVerificationStatus> GetVerificationStatusAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT u.is_email_verified
            FROM users u
            WHERE u.id = @id
            LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new UserVerificationStatus(false);
        }
        return new UserVerificationStatus(
            IsEmailVerified: reader.GetBoolean(0));
    }

    public async Task SetKickedAsync(Guid userId, bool isKicked, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET is_kicked = @isKicked, updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@isKicked", isKicked ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<User>> SearchUsersAsync(string? query, bool? isKicked, bool? isVerified, int limit, int offset, CancellationToken ct = default)
    {
        var conditions = new List<string>();
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add("(email LIKE @query OR name LIKE @query)");
            parameters.Add(new("@query", $"%{query}%"));
        }
        if (isKicked.HasValue)
        {
            conditions.Add("is_kicked = @isKicked");
            parameters.Add(new("@isKicked", isKicked.Value ? 1 : 0));
        }
        if (isVerified.HasValue)
        {
            conditions.Add("is_email_verified = @isVerified");
            parameters.Add(new("@isVerified", isVerified.Value ? 1 : 0));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"""
            SELECT id, email, password_hash, name, phone_no, is_admin, is_email_verified, is_kicked, created_at, updated_at
            FROM users {where}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var users = new List<User>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            users.Add(Map(reader));
        }
        return users;
    }

    public async Task<int> CountUsersAsync(string? query, bool? isKicked, bool? isVerified, CancellationToken ct = default)
    {
        var conditions = new List<string>();
        var parameters = new List<MySqlParameter>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add("(email LIKE @query OR name LIKE @query)");
            parameters.Add(new("@query", $"%{query}%"));
        }
        if (isKicked.HasValue)
        {
            conditions.Add("is_kicked = @isKicked");
            parameters.Add(new("@isKicked", isKicked.Value ? 1 : 0));
        }
        if (isVerified.HasValue)
        {
            conditions.Add("is_email_verified = @isVerified");
            parameters.Add(new("@isVerified", isVerified.Value ? 1 : 0));
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"""SELECT COUNT(*) FROM users {where};""";

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static User Map(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Email = r.GetString(1),
        PasswordHash = r.GetString(2),
        Name = r.GetString(3),
        PhoneNo = r.GetString(4),
        IsAdmin = r.GetBoolean(5),
        IsEmailVerified = r.GetBoolean(6),
        IsKicked = r.GetBoolean(7),
        CreatedAt = r.GetDateTime(8),
        UpdatedAt = r.GetDateTime(9),
    };
}
