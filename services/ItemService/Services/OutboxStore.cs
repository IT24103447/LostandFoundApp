using ItemService.Databases;
using MySqlConnector;

namespace ItemService.Services;

/// <summary>One pending event claimed by the relay.</summary>
public sealed record OutboxRecord(Guid Id, string Topic, string MessageKey, string Payload, int Attempts);

/// <summary>Relay-side access to outbox_events. Each call is its own short, auto-committed statement.</summary>
public interface IOutboxStore
{
    /// <summary>
    /// Reserves up to <paramref name="batchSize"/> due events for this relay for <paramref name="lease"/>
    /// and returns them oldest first. Reserved rows are invisible to other relays until the lease expires.
    /// </summary>
    Task<IReadOnlyList<OutboxRecord>> ClaimBatchAsync(
        int batchSize, int maxAttempts, TimeSpan lease, DateTime nowUtc, CancellationToken ct);

    Task MarkPublishedAsync(Guid id, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records a failed attempt, releases the reservation and schedules the next try.</summary>
    Task MarkFailedAsync(Guid id, string error, DateTime nextAttemptUtc, CancellationToken ct);

    /// <summary>Deletes delivered events older than the cutoff. Returns how many were removed.</summary>
    Task<int> PurgePublishedAsync(DateTime olderThanUtc, CancellationToken ct);
}

public sealed class OutboxStore : IOutboxStore
{
    private readonly IDbConnectionFactory _db;

    public OutboxStore(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<OutboxRecord>> ClaimBatchAsync(
        int batchSize, int maxAttempts, TimeSpan lease, DateTime nowUtc, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString();

        // Step 1: atomically reserve the rows. UPDATE takes row locks, so two app instances racing
        // for the same rows cannot both win; the loser re-checks the WHERE against the winner's write.
        const string claimSql = """
            UPDATE outbox_events
            SET locked_by = @token, locked_until = @leaseUntil
            WHERE published_at IS NULL
              AND attempts < @maxAttempts
              AND next_attempt_at <= @now
              AND (locked_until IS NULL OR locked_until < @now)
            ORDER BY created_at, id
            LIMIT @batchSize;
            """;

        // Step 2: read back exactly the rows this relay reserved.
        const string selectSql = """
            SELECT id, topic, message_key, payload, attempts
            FROM outbox_events
            WHERE locked_by = @token AND published_at IS NULL
            ORDER BY created_at, id;
            """;

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        int claimed;
        await using (var claim = new MySqlCommand(claimSql, conn))
        {
            claim.Parameters.AddWithValue("@token", token);
            claim.Parameters.AddWithValue("@leaseUntil", nowUtc + lease);
            claim.Parameters.AddWithValue("@maxAttempts", maxAttempts);
            claim.Parameters.AddWithValue("@now", nowUtc);
            claim.Parameters.AddWithValue("@batchSize", batchSize);
            claimed = await claim.ExecuteNonQueryAsync(ct);
        }

        if (claimed == 0)
        {
            return Array.Empty<OutboxRecord>();
        }

        var records = new List<OutboxRecord>(claimed);
        await using (var select = new MySqlCommand(selectSql, conn))
        {
            select.Parameters.AddWithValue("@token", token);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(new OutboxRecord(
                    Id: reader.GetGuid(0),
                    Topic: reader.GetString(1),
                    MessageKey: reader.GetString(2),
                    Payload: reader.GetString(3),
                    Attempts: reader.GetInt32(4)));
            }
        }

        return records;
    }

    public async Task MarkPublishedAsync(Guid id, DateTime nowUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE outbox_events
            SET published_at = @now, last_error = NULL, locked_by = NULL, locked_until = NULL
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@now", nowUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, string error, DateTime nextAttemptUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE outbox_events
            SET attempts = attempts + 1,
                next_attempt_at = @next,
                last_error = @error,
                locked_by = NULL,
                locked_until = NULL
            WHERE id = @id;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@next", nextAttemptUtc);
        cmd.Parameters.AddWithValue("@error", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PurgePublishedAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM outbox_events
            WHERE published_at IS NOT NULL AND published_at < @cutoff
            LIMIT 1000;
            """;
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cutoff", olderThanUtc);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
