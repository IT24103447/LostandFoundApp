using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Matches;

public sealed record ConfirmationDelivery(
    Guid EventId, Guid MatchId, Guid LeaseToken, string Topic, string Payload, int Attempts);

public sealed class MatchConfirmationDeliveryStore(IDbConnectionFactory connections)
{
    public async Task<ConfirmationDelivery?> ClaimAsync(CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using (var claim = new MySqlCommand("""
            UPDATE match_confirmation_outbox
            SET lease_token = @token, lease_until = @until
            WHERE published_at IS NULL AND next_attempt_at <= @now
              AND (lease_until IS NULL OR lease_until < @now)
            ORDER BY created_at, event_id LIMIT 1;
            """, connection))
        {
            claim.Parameters.AddWithValue("@token", token);
            claim.Parameters.AddWithValue("@until", now.AddMinutes(2));
            claim.Parameters.AddWithValue("@now", now);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }

        await using var select = new MySqlCommand("""
            SELECT event_id, match_id, topic, payload, attempts
            FROM match_confirmation_outbox WHERE lease_token = @token;
            """, connection);
        select.Parameters.AddWithValue("@token", token);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ConfirmationDelivery(reader.GetGuid(0), reader.GetGuid(1), token,
                reader.GetString(2), reader.GetString(3), reader.GetInt32(4))
            : null;
    }

    public async Task CompleteAsync(ConfirmationDelivery delivery, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE match_confirmation_outbox
            SET published_at = @now, lease_token = NULL, lease_until = NULL, error_code = NULL
            WHERE event_id = @id AND lease_token = @token AND published_at IS NULL;
            """, connection);
        command.Parameters.AddWithValue("@id", delivery.EventId);
        command.Parameters.AddWithValue("@token", delivery.LeaseToken);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryAsync(ConfirmationDelivery delivery, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(delivery.Attempts, 6)));
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE match_confirmation_outbox
            SET attempts = LEAST(attempts + 1, 1000000), next_attempt_at = @next,
                lease_token = NULL, lease_until = NULL, error_code = 'MATCH_EVENT_DELIVERY_FAILED'
            WHERE event_id = @id AND lease_token = @token AND published_at IS NULL;
            """, connection);
        command.Parameters.AddWithValue("@id", delivery.EventId);
        command.Parameters.AddWithValue("@token", delivery.LeaseToken);
        command.Parameters.AddWithValue("@next", DateTime.UtcNow.AddSeconds(seconds));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
