using System.Data;
using System.Text.Json;
using MatchingService.Databases;
using MatchingService.Models;
using MySqlConnector;

namespace MatchingService.Repositories;

public sealed class ImageDescriptionRepository
    : IImageDescriptionRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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
            INSERT INTO image_descriptions (
                id,
                source_event_id,
                photo_key,
                item_id,
                item_type,
                blob_url,
                processing_status,
                attempts,
                next_retry_at,
                created_at,
                updated_at
            )
            VALUES (
                @id,
                @sourceEventId,
                @photoKey,
                @itemId,
                @itemType,
                @blobUrl,
                'PENDING',
                0,
                @nextRetryAt,
                @createdAt,
                @updatedAt
            );
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", record.Id.ToString());
        command.Parameters.AddWithValue(
            "@sourceEventId", record.SourceEventId.ToString());
        command.Parameters.AddWithValue("@photoKey", record.PhotoKey);
        command.Parameters.AddWithValue("@itemId", record.ItemId.ToString());
        command.Parameters.AddWithValue(
            "@itemType", record.ItemType.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("@blobUrl", record.BlobUrl);
        command.Parameters.AddWithValue(
            "@nextRetryAt", (object?)record.NextRetryAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", record.CreatedAt);
        command.Parameters.AddWithValue("@updatedAt", record.UpdatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            // At-least-once delivery can repeat a URL. Existing rows,
            // including completed descriptions, must remain unchanged.
            return false;
        }
    }

    public async Task<ClaimedImageDescription?> ClaimNextAsync(
        int maxAttempts,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        const string expireSql = """
            UPDATE image_descriptions
            SET processing_status = 'FAILED',
                error_code = 'WORKER_LEASE_EXPIRED',
                next_retry_at = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE processing_status = 'PROCESSING'
              AND lease_expires_at <= UTC_TIMESTAMP(3)
              AND attempts >= @maxAttempts;
            """;

        await using (var expire = new MySqlCommand(
                         expireSql, connection, transaction))
        {
            expire.Parameters.AddWithValue("@maxAttempts", maxAttempts);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        const string selectSql = """
            SELECT id, blob_url, attempts
            FROM image_descriptions
            WHERE attempts < @maxAttempts
              AND (
                  (
                      processing_status = 'PENDING'
                      AND (
                          next_retry_at IS NULL
                          OR next_retry_at <= UTC_TIMESTAMP(3)
                      )
                  )
                  OR (
                      processing_status = 'PROCESSING'
                      AND lease_expires_at <= UTC_TIMESTAMP(3)
                  )
              )
            ORDER BY created_at, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """;

        Guid id;
        string blobUrl;
        int attempts;

        await using (var select = new MySqlCommand(
                         selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("@maxAttempts", maxAttempts);

            await using var reader = await select.ExecuteReaderAsync(
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            id = reader.GetGuid(0);
            blobUrl = reader.GetString(1);
            attempts = reader.GetInt32(2) + 1;
        }

        var leaseToken = Guid.NewGuid();

        const string claimSql = """
            UPDATE image_descriptions
            SET processing_status = 'PROCESSING',
                attempts = @attempts,
                lease_token = @leaseToken,
                lease_expires_at = TIMESTAMPADD(
                    SECOND, @leaseSeconds, UTC_TIMESTAMP(3)),
                next_retry_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id;
            """;

        await using (var claim = new MySqlCommand(
                         claimSql, connection, transaction))
        {
            claim.Parameters.AddWithValue("@id", id.ToString());
            claim.Parameters.AddWithValue("@attempts", attempts);
            claim.Parameters.AddWithValue(
                "@leaseToken", leaseToken.ToString());
            claim.Parameters.AddWithValue("@leaseSeconds", leaseSeconds);

            await claim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new ClaimedImageDescription(
            id,
            leaseToken,
            blobUrl,
            attempts);
    }

    public async Task<bool> CompleteAsync(
        ClaimedImageDescription job,
        GeneratedImageDescription description,
        string modelName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE image_descriptions
            SET description = @description,
                attributes_json = @attributesJson,
                processing_status = 'COMPLETED',
                model_name = @modelName,
                processed_at = UTC_TIMESTAMP(3),
                error_code = NULL,
                next_retry_at = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id
              AND processing_status = 'PROCESSING'
              AND lease_token = @leaseToken;
            """;

        var attributesJson = JsonSerializer.Serialize(
            new
            {
                description.ObjectType,
                description.Colours,
                description.Materials,
                description.VisibleBrand,
                description.Patterns,
                description.Condition,
                description.DistinctiveFeatures,
                description.Uncertainty
            },
            JsonOptions);

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", job.Id.ToString());
        command.Parameters.AddWithValue(
            "@leaseToken", job.LeaseToken.ToString());
        command.Parameters.AddWithValue(
            "@description", description.Description);
        command.Parameters.AddWithValue("@attributesJson", attributesJson);
        command.Parameters.AddWithValue("@modelName", modelName);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> RecordFailureAsync(
        ClaimedImageDescription job,
        string errorCode,
        DateTime? nextRetryAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE image_descriptions
            SET processing_status = @status,
                error_code = @errorCode,
                next_retry_at = @nextRetryAt,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id
              AND processing_status = 'PROCESSING'
              AND lease_token = @leaseToken;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", job.Id.ToString());
        command.Parameters.AddWithValue(
            "@leaseToken", job.LeaseToken.ToString());
        command.Parameters.AddWithValue(
            "@status", nextRetryAt.HasValue ? "PENDING" : "FAILED");
        command.Parameters.AddWithValue("@errorCode", errorCode);
        command.Parameters.AddWithValue(
            "@nextRetryAt", (object?)nextRetryAt ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}