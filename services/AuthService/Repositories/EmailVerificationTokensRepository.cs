using AuthService.Databases;
using AuthService.Models;
using MySqlConnector;

namespace AuthService.Repositories;

public class EmailVerificationTokensRepository : IEmailVerificationTokensRepository
{
    private readonly IDbConnectionFactory _db;

    public EmailVerificationTokensRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task CreateAsync(
        Guid userId,
        string codeHash,
        string? pendingEmail,
        DateTime expiresAt,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO email_verification_tokens
                (id, user_id, code_hash, pending_email, expires_at, attempts)
            VALUES
                (@id, @userId, @codeHash, @pendingEmail, @expiresAt, 0);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@userId", userId.ToString());
        cmd.Parameters.AddWithValue("@codeHash", codeHash);
        cmd.Parameters.AddWithValue("@pendingEmail", (object?)pendingEmail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@expiresAt", expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EmailVerificationToken?> GetActiveByHashAsync(string codeHash, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, user_id, code_hash, pending_email, expires_at, attempts, used_at, created_at, bounced_at
            FROM email_verification_tokens
            WHERE code_hash = @codeHash
              AND used_at IS NULL
              AND bounced_at IS NULL
              AND expires_at > UTC_TIMESTAMP(3)
              AND attempts < 5
            LIMIT 1;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@codeHash", codeHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<EmailVerificationToken?> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, user_id, code_hash, pending_email, expires_at, attempts, used_at, created_at, bounced_at
            FROM email_verification_tokens
            WHERE user_id = @userId
              AND used_at IS NULL
              AND bounced_at IS NULL
              AND expires_at > UTC_TIMESTAMP(3)
            ORDER BY created_at DESC
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
            UPDATE email_verification_tokens
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
            UPDATE email_verification_tokens
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
            UPDATE email_verification_tokens
            SET used_at = UTC_TIMESTAMP(3)
            WHERE user_id = @userId AND used_at IS NULL AND bounced_at IS NULL;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@userId", userId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MarkLatestBouncedForEmailAsync(string email, CancellationToken ct = default)
    {
        // Marks the most recent active token (matching either user.email or pending_email)
        // as bounced. Returns row count so the caller can decide whether to also flag the user.
        // The inner SELECT is wrapped in another SELECT to force MySQL to materialize it,
        // sidestepping the "can't specify target table for update in FROM clause" restriction.
        const string sql = """
            UPDATE email_verification_tokens t
            INNER JOIN users u ON u.id = t.user_id
            SET t.bounced_at = UTC_TIMESTAMP(3)
            WHERE t.used_at IS NULL
              AND t.bounced_at IS NULL
              AND (u.email = @email OR t.pending_email = @email)
              AND t.id = (
                  SELECT id FROM (
                      SELECT t2.id
                      FROM email_verification_tokens t2
                      WHERE t2.user_id = t.user_id
                        AND t2.used_at IS NULL
                        AND t2.bounced_at IS NULL
                      ORDER BY t2.created_at DESC
                      LIMIT 1
                  ) AS latest
              );
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@email", email);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static EmailVerificationToken Map(MySqlDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        UserId = Guid.Parse(r.GetString(1)),
        CodeHash = r.GetString(2),
        PendingEmail = r.IsDBNull(3) ? null : r.GetString(3),
        ExpiresAt = r.GetDateTime(4),
        Attempts = r.GetInt32(5),
        UsedAt = r.IsDBNull(6) ? null : r.GetDateTime(6),
        CreatedAt = r.GetDateTime(7),
        BouncedAt = r.IsDBNull(8) ? null : r.GetDateTime(8),
    };
}
