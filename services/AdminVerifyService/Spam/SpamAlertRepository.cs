using AdminVerifyService.Databases;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public sealed record SpamAlertJob(
    Guid Id,
    string RecipientEmail,
    int Attempts);

public sealed class SpamAlertRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    public async Task<SpamAlertJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT id, recipient_email, attempts, next_attempt_at
            FROM spam_record_notifications
            WHERE status = 'PENDING'
              AND next_attempt_at <= @now
            ORDER BY next_attempt_at, id
            LIMIT 1;
            """;

        const string claimSql = """
            UPDATE spam_record_notifications
            SET attempts = attempts + 1,
                next_attempt_at = @leaseUntil,
                updated_at = @now
            WHERE id = @id
              AND status = 'PENDING'
              AND next_attempt_at = @seenNextAttemptAt;
            """;

        var now = time.GetUtcNow().UtcDateTime;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        SpamAlertJob job;
        DateTime seenNextAttemptAt;

        await using (var find = new MySqlCommand(findSql, connection))
        {
            find.Parameters.AddWithValue("@now", now);

            await using var reader = await find.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            job = new SpamAlertJob(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2) + 1);
            seenNextAttemptAt = reader.GetDateTime(3);
        }

        await using var claim = new MySqlCommand(claimSql, connection);
        claim.Parameters.AddWithValue("@id", job.Id);
        claim.Parameters.AddWithValue("@leaseUntil", now.Add(ClaimLease));
        claim.Parameters.AddWithValue("@now", now);
        claim.Parameters.AddWithValue("@seenNextAttemptAt", seenNextAttemptAt);

        return await claim.ExecuteNonQueryAsync(cancellationToken) == 1 ? job : null;
    }

    public async Task MarkSentAsync(SpamAlertJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE spam_record_notifications
            SET status = 'SENT',
                sent_at = @now,
                next_attempt_at = NULL,
                updated_at = @now
            WHERE id = @id;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAttemptAsync(SpamAlertJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE spam_record_notifications
            SET status = @status,
                next_attempt_at = @nextAttemptAt,
                updated_at = @now
            WHERE id = @id;
            """;

        var now = time.GetUtcNow().UtcDateTime;
        var giveUp = job.Attempts >= MaxAttempts;
        var retryDelay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, job.Attempts)));

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@status", giveUp ? "FAILED" : "PENDING");
        command.Parameters.AddWithValue("@nextAttemptAt", giveUp ? DBNull.Value : now.Add(retryDelay));
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
