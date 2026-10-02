using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.HiddenInformation;

public sealed class ItemHiddenInformationRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    public async Task SaveAsync(
        string itemType,
        Guid itemId,
        string hiddenInformation,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO matching_item_hidden_information (
                item_type, item_id, hidden_information,
                source_occurred_at, updated_at
            )
            VALUES (
                @itemType, @itemId, @hidden,
                @occurredAt, @now
            ) AS incoming
            ON DUPLICATE KEY UPDATE
                hidden_information = IF(
                    incoming.source_occurred_at >=
                        matching_item_hidden_information.source_occurred_at,
                    incoming.hidden_information,
                    matching_item_hidden_information.hidden_information),
                source_occurred_at = GREATEST(
                    matching_item_hidden_information.source_occurred_at,
                    incoming.source_occurred_at),
                updated_at = incoming.updated_at;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@itemType", itemType);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@hidden", hiddenInformation);
        command.Parameters.AddWithValue("@occurredAt", occurredAt);
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string? Lost, string? Found)> GetPairAsync(
        Guid lostItemId,
        Guid foundItemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item_type, hidden_information
            FROM matching_item_hidden_information
            WHERE (item_type = 'LOST' AND item_id = @lostItemId)
               OR (item_type = 'FOUND' AND item_id = @foundItemId);
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);

        string? lost = null;
        string? found = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == "LOST")
            {
                lost = reader.GetString(1);
            }
            else
            {
                found = reader.GetString(1);
            }
        }

        return (lost, found);
    }
}
