using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Notifications;

public sealed class NotificationRepository(IDbConnectionFactory connections)
{
    private const int MaxAttempts = 5;

    public async Task<NotificationJob?> ClaimNextAsync(
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        // The atomic update reserves one job across multiple service instances.
        // Count the attempt now so repeated process crashes cannot retry forever.
        const string claimSql = """
            UPDATE match_notifications
            SET lease_token = @leaseToken,
                lease_expires_at = DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 5 MINUTE),
                attempts = attempts + 1,
                updated_at = UTC_TIMESTAMP(3)
            WHERE status IN ('PENDING', 'FAILED')
              AND attempts < @maxAttempts
              AND next_attempt_at <= UTC_TIMESTAMP(3)
              AND (
                  lease_expires_at IS NULL
                  OR lease_expires_at <= UTC_TIMESTAMP(3)
              )
            ORDER BY created_at, id
            LIMIT 1;
            """;

        await using (var command = new MySqlCommand(claimSql, connection))
        {
            command.Parameters.AddWithValue("@leaseToken", leaseToken);
            command.Parameters.AddWithValue("@maxAttempts", MaxAttempts);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return null;
            }
        }

        const string selectSql = """
            SELECT id, match_id, recipient_user_id, notification_type, attempts
            FROM match_notifications
            WHERE lease_token = @leaseToken;
            """;

        await using var select = new MySqlCommand(selectSql, connection);
        select.Parameters.AddWithValue("@leaseToken", leaseToken);

        await using var reader =
            await select.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NotificationJob(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetInt32(4),
            leaseToken);
    }

    public async Task<NotificationRecipient> GetRecipientAsync(
        NotificationJob job,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                m.status,
                m.is_active,
                m.claimant_role,
                m.lost_reporter_id,
                m.finder_id,
                c.email,
                c.is_active,
                c.is_deleted
            FROM match_notifications AS n
            JOIN matches AS m ON m.id = n.match_id
            LEFT JOIN notification_contacts AS c
                ON c.user_id = n.recipient_user_id
            WHERE n.id = @id
              AND n.lease_token = @leaseToken
              AND n.lease_expires_at > UTC_TIMESTAMP(3)
              AND n.status IN ('PENDING', 'FAILED');
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@leaseToken", job.LeaseToken);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new NotificationRecipient(
                null, false, "NOTIFICATION_LEASE_LOST");
        }

        var status = reader.GetString(0);
        var active = reader.GetBoolean(1);
        var claimantRole = reader.GetString(2);
        var lostReporterId = reader.GetGuid(3);
        var finderId = reader.GetGuid(4);

        if (!active)
        {
            return new NotificationRecipient(
                null, false, "MATCH_INACTIVE");
        }

        var isParticipant =
            job.RecipientUserId == lostReporterId ||
            job.RecipientUserId == finderId;

        var relevant = job.Type switch
        {
            NotificationTypes.CounterpartAction =>
                (claimantRole == "LOST" &&
                 status == "LOST_REPORTER_CONFIRMED" &&
                 job.RecipientUserId == finderId) ||
                (claimantRole == "FOUND" &&
                 status == "FINDER_CONFIRMED" &&
                 job.RecipientUserId == lostReporterId),

            NotificationTypes.MatchConfirmed =>
                isParticipant && status == "CONFIRMED",

            NotificationTypes.MatchRejected =>
                isParticipant && status == "REJECTED",

            _ => false
        };

        if (!relevant)
        {
            return new NotificationRecipient(
                null, false, "NOTIFICATION_NO_LONGER_RELEVANT");
        }

        if (reader.IsDBNull(5))
        {
            return new NotificationRecipient(null, true, null);
        }

        if (!reader.GetBoolean(6) || reader.GetBoolean(7))
        {
            return new NotificationRecipient(
                null, false, "RECIPIENT_INACTIVE");
        }

        return new NotificationRecipient(reader.GetString(5), true, null);
    }

    public Task<bool> MarkSentAsync(
        NotificationJob job,
        CancellationToken cancellationToken)
    {
        return SaveResultAsync(
            job, "SENT", null, null, cancellationToken);
    }

    public Task<bool> MarkCancelledAsync(
        NotificationJob job,
        string code,
        CancellationToken cancellationToken)
    {
        return SaveResultAsync(
            job, "CANCELLED", code, null, cancellationToken);
    }

    public Task<bool> MarkFailedAsync(
        NotificationJob job,
        string code,
        CancellationToken cancellationToken)
    {
        int? retrySeconds = job.Attempts < MaxAttempts
            ? (int)Math.Min(1800, 30 * Math.Pow(2, job.Attempts - 1))
            : null;

        return SaveResultAsync(
            job, "FAILED", code, retrySeconds, cancellationToken);
    }

    private async Task<bool> SaveResultAsync(
        NotificationJob job,
        string status,
        string? code,
        int? retrySeconds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE match_notifications
            SET status = @status,
                sent_at = CASE
                    WHEN @status = 'SENT' THEN UTC_TIMESTAMP(3)
                    ELSE sent_at
                END,
                next_attempt_at = CASE
                    WHEN @retrySeconds IS NULL THEN NULL
                    ELSE TIMESTAMPADD(
                        SECOND, @retrySeconds, UTC_TIMESTAMP(3))
                END,
                lease_token = NULL,
                lease_expires_at = NULL,
                error_code = @code,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id
              AND lease_token = @leaseToken
              AND status IN ('PENDING', 'FAILED');
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@leaseToken", job.LeaseToken);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@retrySeconds", (object?)retrySeconds ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}