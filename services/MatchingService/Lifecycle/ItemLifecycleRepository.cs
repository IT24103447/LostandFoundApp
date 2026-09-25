using System.Data;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Lifecycle;

public sealed record ItemLifecycleEvent(
    Guid EventId,
    Guid ItemId,
    string ItemType,
    string Reason,
    DateTime OccurredAt);

public sealed class ItemLifecycleRepository(IDbConnectionFactory connections)
{
    public async Task ApplyAsync(
        ItemLifecycleEvent itemEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // Claim creation takes the same item lock. A claim cannot be inserted
        // after this transaction has made the item inactive.
        var existingReason = await LockItemAsync(
            connection, transaction,
            itemEvent.ItemType, itemEvent.ItemId, cancellationToken);

        const string eventSql = """
            INSERT IGNORE INTO matching_item_lifecycle_events (
                event_id, item_type, item_id, reason,
                occurred_at, processed_at
            )
            VALUES (
                @eventId, @type, @itemId, @reason,
                @occurredAt, UTC_TIMESTAMP(3)
            );
            """;

        await using (var command = new MySqlCommand(
            eventSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", itemEvent.EventId);
            command.Parameters.AddWithValue("@type", itemEvent.ItemType);
            command.Parameters.AddWithValue("@itemId", itemEvent.ItemId);
            command.Parameters.AddWithValue("@reason", itemEvent.Reason);
            command.Parameters.AddWithValue("@occurredAt", itemEvent.OccurredAt);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        // Resolve and delete are terminal for this story. A delayed resolve
        // event must never reverse an already processed deletion.
        var effectiveReason =
            existingReason == "DELETED" || itemEvent.Reason == "DELETED"
                ? "DELETED"
                : "RESOLVED";

        if (existingReason is null ||
            (existingReason != "DELETED" && effectiveReason == "DELETED"))
        {
            const string stateSql = """
                UPDATE matching_item_states
                SET inactive_reason = @reason,
                    source_event_id = @eventId,
                    source_occurred_at = @occurredAt,
                    updated_at = UTC_TIMESTAMP(3)
                WHERE item_type = @type AND item_id = @itemId;
                """;

            await using var state = new MySqlCommand(
                stateSql, connection, transaction);

            state.Parameters.AddWithValue("@reason", effectiveReason);
            state.Parameters.AddWithValue("@eventId", itemEvent.EventId);
            state.Parameters.AddWithValue("@occurredAt", itemEvent.OccurredAt);
            state.Parameters.AddWithValue("@type", itemEvent.ItemType);
            state.Parameters.AddWithValue("@itemId", itemEvent.ItemId);

            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        var itemColumn = itemEvent.ItemType == "LOST"
            ? "lost_item_id"
            : "found_item_id";

        var deactivateSql = $"""
            UPDATE matches
            SET is_active = 0,
                deactivated_at = UTC_TIMESTAMP(3),
                deactivation_reason = @reason,
                deactivated_item_id = @itemId,
                deactivated_item_type = @type,
                updated_at = UTC_TIMESTAMP(3)
            WHERE {itemColumn} = @itemId
              AND is_active = 1
              AND status IN (
                  'AWAITING_CLAIMANT_CONFIRMATION',
                  'LOST_REPORTER_CONFIRMED',
                  'FINDER_CONFIRMED'
              );
            """;

        await using (var deactivate = new MySqlCommand(
            deactivateSql, connection, transaction))
        {
            deactivate.Parameters.AddWithValue(
                "@reason", $"ITEM_{effectiveReason}");
            deactivate.Parameters.AddWithValue("@itemId", itemEvent.ItemId);
            deactivate.Parameters.AddWithValue("@type", itemEvent.ItemType);

            await deactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        // SENT records remain as delivery history. Pending retries are stopped.
        var cancelSql = $"""
            UPDATE match_notifications AS n
            JOIN matches AS m ON m.id = n.match_id
            SET n.status = 'CANCELLED',
                n.next_attempt_at = NULL,
                n.lease_token = NULL,
                n.lease_expires_at = NULL,
                n.error_code = 'MATCH_INACTIVE',
                n.updated_at = UTC_TIMESTAMP(3)
            WHERE m.{itemColumn} = @itemId
              AND m.is_active = 0
              AND n.status IN ('PENDING', 'FAILED');
            """;

        await using (var cancel = new MySqlCommand(
            cancelSql, connection, transaction))
        {
            cancel.Parameters.AddWithValue("@itemId", itemEvent.ItemId);
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task EnsurePairActiveAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Guid lostItemId,
        Guid foundItemId,
        CancellationToken cancellationToken)
    {
        // Always take these locks in the same order.
        var lostReason = await LockItemAsync(
            connection, transaction, "LOST", lostItemId, cancellationToken);

        var foundReason = await LockItemAsync(
            connection, transaction, "FOUND", foundItemId, cancellationToken);

        if (lostReason is not null || foundReason is not null)
        {
            throw new ClaimException(
                409, "One of these reports has been resolved or deleted.");
        }
    }

    private static async Task<string?> LockItemAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string itemType,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO matching_item_states (
                item_type, item_id, updated_at
            )
            VALUES (@type, @itemId, UTC_TIMESTAMP(3))
            ON DUPLICATE KEY UPDATE item_id = matching_item_states.item_id;
            """;

        await using (var insert = new MySqlCommand(
            insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("@type", itemType);
            insert.Parameters.AddWithValue("@itemId", itemId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        const string selectSql = """
            SELECT inactive_reason
            FROM matching_item_states
            WHERE item_type = @type AND item_id = @itemId
            FOR UPDATE;
            """;

        await using var select = new MySqlCommand(
            selectSql, connection, transaction);

        select.Parameters.AddWithValue("@type", itemType);
        select.Parameters.AddWithValue("@itemId", itemId);

        return await select.ExecuteScalarAsync(cancellationToken) as string;
    }
}