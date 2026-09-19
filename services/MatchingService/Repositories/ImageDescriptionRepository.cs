using MatchingService.Databases;
using MatchingService.Models;
using MySqlConnector;

namespace MatchingService.Repositories;

public sealed class ImageDescriptionRepository : IImageDescriptionRepository
{
    private readonly IDbConnectionFactory _connections;

    public ImageDescriptionRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<bool> CreatePendingAsync(
        ImageDescriptionRecord record,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO image_descriptions
                (id, source_event_id, photo_key, item_id, item_type, blob_url,
                 processing_status, attempts, next_retry_at, created_at, updated_at)
            VALUES
                (@id, @sourceEventId, @photoKey, @itemId, @itemType, @blobUrl,
                 'PENDING', 0, @nextRetryAt, @createdAt, @updatedAt)
            ON DUPLICATE KEY UPDATE photo_key = photo_key;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", record.Id.ToString());
        command.Parameters.AddWithValue("@sourceEventId", record.SourceEventId.ToString());
        command.Parameters.AddWithValue("@photoKey", record.PhotoKey);
        command.Parameters.AddWithValue("@itemId", record.ItemId.ToString());
        command.Parameters.AddWithValue("@itemType", record.ItemType.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("@blobUrl", record.BlobUrl);
        command.Parameters.AddWithValue("@nextRetryAt", record.NextRetryAt);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt);
        command.Parameters.AddWithValue("@updatedAt", record.UpdatedAt);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
