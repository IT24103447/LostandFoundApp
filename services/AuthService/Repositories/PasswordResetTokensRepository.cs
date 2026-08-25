using AuthService.Databases;
using AuthService.Models;
using MySqlConnector;

namespace AuthService.Repositories;

public class PasswordResetTokensRepository : IPasswordResetTokensRepository
{
    private readonly IDbConnectionFactory _db;

    public PasswordResetTokensRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task CreateAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO password_reset_tokens
                (id, user_id, code_hash, expires_at, attempts, used_at, created_at)
            VALUES
                (@id, @userId, @codeHash, @expiresAt, @attempts, @usedAt, @createdAt);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", token.Id.ToString());
        cmd.Parameters.AddWithValue("@userId", token.UserId.ToString());
        cmd.Parameters.AddWithValue("@codeHash", token.CodeHash);
        cmd.Parameters.AddWithValue("@expiresAt", token.ExpiresAt);
        cmd.Parameters.AddWithValue("@attempts", token.Attempts);
        cmd.Parameters.AddWithValue("@usedAt", token.UsedAt == null ? DBNull.Value : token.UsedAt);
        cmd.Parameters.AddWithValue("@createdAt", token.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PasswordResetToken?> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, user_id, code_hash, expires_at, attempts, used_at, created_at
            FROM password_reset_tokens
            WHERE user_id = @userId
              AND used_at IS NULL
              AND expires_at > UTC_TIMESTAMP(3)
            LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task IncrementAttemptsAsync(Guid tokenId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE password_reset_tokens
            SET attempts = attempts + 1
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", tokenId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkUsedAsync(Guid tokenId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE password_reset_tokens
            SET used_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", tokenId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InvalidateAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE password_reset_tokens
            SET used_at = UTC_TIMESTAMP(3)
            WHERE user_id = @userId AND used_at IS NULL;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PasswordResetToken Map(MySqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        UserId = r.GetGuid(1),
        CodeHash = r.GetString(2),
        ExpiresAt = r.GetDateTime(3),
        Attempts = r.GetInt32(4),
        UsedAt = r.IsDBNull(5) ? null : r.GetDateTime(5),
        CreatedAt = r.GetDateTime(6),
    };
}
