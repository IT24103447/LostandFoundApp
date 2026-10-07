using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Appeals;

public sealed record AppealEmailJob(
    Guid Id,
    string Type,
    string RecipientEmail,
    int Attempts,
    string? RejectionReason,
    Guid? MatchId);

public sealed class AppealNotificationRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    public async Task<AppealEmailJob?> ClaimNextAsync(
        CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT n.id, n.notification_type, n.recipient_email, n.attempts,
                   n.next_attempt_at, a.rejection_reason, m.id
            FROM appeal_notifications AS n
            JOIN match_appeals AS a ON a.id = n.appeal_id
            LEFT JOIN matches AS m
                ON m.lost_item_id = a.lost_item_id
               AND m.found_item_id = a.found_item_id
            WHERE n.status = 'PENDING'
              AND n.next_attempt_at <= @now
              AND (n.notification_type = 'APPEAL_REJECTED' OR m.id IS NOT NULL)
            ORDER BY n.next_attempt_at, n.id
            LIMIT 1;
            """;

        const string claimSql = """
            UPDATE appeal_notifications
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

        AppealEmailJob job;
        DateTime seenNextAttemptAt;

        await using (var find = new MySqlCommand(findSql, connection))
        {
            find.Parameters.AddWithValue("@now", now);

            await using var reader = await find.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            seenNextAttemptAt = reader.GetDateTime(4);
            job = new AppealEmailJob(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) + 1,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6));
        }

        await using var claim = new MySqlCommand(claimSql, connection);
        claim.Parameters.AddWithValue("@id", job.Id);
        claim.Parameters.AddWithValue("@leaseUntil", now.Add(ClaimLease));
        claim.Parameters.AddWithValue("@now", now);
        claim.Parameters.AddWithValue("@seenNextAttemptAt", seenNextAttemptAt);

        return await claim.ExecuteNonQueryAsync(cancellationToken) == 1 ? job : null;
    }

    public async Task MarkSentAsync(
        AppealEmailJob job,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appeal_notifications
            SET status = 'SENT',
                match_id = @matchId,
                sent_at = @now,
                next_attempt_at = NULL,
                updated_at = @now
            WHERE id = @id;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@matchId", job.MatchId.HasValue ? job.MatchId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkFailedAttemptAsync(
        AppealEmailJob job,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE appeal_notifications
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
