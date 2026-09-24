using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Notifications;

public sealed class NotificationDiscoveryService(
    IDbConnectionFactory connections)
{
    public async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        // A crashed worker may have used its final attempt without saving
        // a delivery result. Do not retry indefinitely.
        const string exhaustedSql = """
            UPDATE match_notifications
            SET status = 'FAILED',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                error_code = 'DELIVERY_STATE_UNKNOWN',
                updated_at = UTC_TIMESTAMP(3)
            WHERE status IN ('PENDING', 'FAILED')
              AND attempts >= 5
              AND next_attempt_at IS NOT NULL
              AND (
                  lease_expires_at IS NULL
                  OR lease_expires_at <= UTC_TIMESTAMP(3)
              );
            """;

        await using (var command = new MySqlCommand(exhaustedSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Story 2 confirms the claimant when they submit the claim.
        // Do not queue an action request for an already closed match.
        const string counterpartSql = """
            INSERT IGNORE INTO match_notifications (
                id, match_id, recipient_user_id, notification_type,
                status, next_attempt_at, created_at, updated_at
            )
            SELECT
                UUID(),
                m.id,
                CASE
                    WHEN m.claimant_role = 'LOST' THEN m.finder_id
                    ELSE m.lost_reporter_id
                END,
                'COUNTERPART_ACTION',
                'PENDING',
                UTC_TIMESTAMP(3),
                UTC_TIMESTAMP(3),
                UTC_TIMESTAMP(3)
            FROM matches AS m
            JOIN match_notification_activation AS activation
                ON activation.id = 1
            WHERE m.created_at >= activation.activated_at
              AND m.is_active = 1
              AND (
                  (m.claimant_role = 'LOST'
                   AND m.status = 'LOST_REPORTER_CONFIRMED')
                  OR
                  (m.claimant_role = 'FOUND'
                   AND m.status = 'FINDER_CONFIRMED')
              );
            """;

        await using (var command = new MySqlCommand(counterpartSql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // The unique constraint prevents repeated discovery of an audit
        // action from creating another notification for the same recipient.
        const string outcomeSql = """
            INSERT IGNORE INTO match_notifications (
                id, match_id, recipient_user_id, notification_type,
                status, next_attempt_at, created_at, updated_at
            )
            SELECT
                UUID(),
                m.id,
                CASE
                    WHEN recipient.side = 0 THEN m.lost_reporter_id
                    ELSE m.finder_id
                END,
                CASE
                    WHEN a.new_status = 'CONFIRMED'
                    THEN 'MATCH_CONFIRMED'
                    ELSE 'MATCH_REJECTED'
                END,
                'PENDING',
                UTC_TIMESTAMP(3),
                UTC_TIMESTAMP(3),
                UTC_TIMESTAMP(3)
            FROM match_actions AS a
            JOIN matches AS m ON m.id = a.match_id
            JOIN match_notification_activation AS activation
                ON activation.id = 1
            CROSS JOIN (
                SELECT 0 AS side
                UNION ALL
                SELECT 1 AS side
            ) AS recipient
            WHERE a.created_at >= activation.activated_at
              AND a.new_status IN ('CONFIRMED', 'REJECTED')
              AND m.status = a.new_status
              AND m.is_active = 1;
            """;

        await using var outcomeCommand =
            new MySqlCommand(outcomeSql, connection);

        await outcomeCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}