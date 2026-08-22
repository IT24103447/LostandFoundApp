using AuthService.Databases;
using MySqlConnector;
using System.Text.Json;

namespace AuthService.Repositories;

public interface IEmailBouncesRepository
{
    Task RecordAsync(
        Guid? userId,
        string email,
        string eventType,
        string? reason,
        string? sgMessageId,
        DateTime occurredAt,
        string? rawPayloadJson,
        CancellationToken ct = default);
}

public class EmailBouncesRepository : IEmailBouncesRepository
{
    private readonly IDbConnectionFactory _db;

    public EmailBouncesRepository(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task RecordAsync(
        Guid? userId,
        string email,
        string eventType,
        string? reason,
        string? sgMessageId,
        DateTime occurredAt,
        string? rawPayloadJson,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO email_bounces
                (id, user_id, email, event_type, reason, sg_message_id, occurred_at, raw_payload)
            VALUES
                (@id, @userId, @email, @eventType, @reason, @sgMessageId, @occurredAt, @raw);
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@userId", (object?)userId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@eventType", eventType);
        cmd.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sgMessageId", (object?)sgMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@occurredAt", occurredAt);
        cmd.Parameters.AddWithValue("@raw", (object?)rawPayloadJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
