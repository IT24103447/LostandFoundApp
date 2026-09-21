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
                source_event_type,
                source_occurred_at,
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
                @sourceEventType,
                @sourceOccurredAt,
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

        command.Parameters.AddWithValue(
            "@id",
            record.Id);

        command.Parameters.AddWithValue(
            "@sourceEventId",
            record.SourceEventId);

        command.Parameters.AddWithValue(
            "@sourceEventType",
            record.SourceEventType.ToString().ToUpperInvariant());

        command.Parameters.AddWithValue(
            "@sourceOccurredAt",
            record.SourceOccurredAt);

        command.Parameters.AddWithValue(
            "@photoKey",
            record.PhotoKey);

        command.Parameters.AddWithValue(
            "@itemId",
            record.ItemId);

        command.Parameters.AddWithValue(
            "@itemType",
            record.ItemType.ToString().ToUpperInvariant());

        command.Parameters.AddWithValue(
            "@blobUrl",
            record.BlobUrl);

        command.Parameters.AddWithValue(
            "@nextRetryAt",
            (object?)record.NextRetryAt ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "@createdAt",
            record.CreatedAt);

        command.Parameters.AddWithValue(
            "@updatedAt",
            record.UpdatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            // Repeated Kafka delivery of the same Blob URL is expected.
            // The unique photo_key keeps the operation idempotent.
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

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        const string expireLeaseSql = """
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

        await using (var expireLease = new MySqlCommand(
                        expireLeaseSql,
                        connection,
                        transaction))
        {
            expireLease.Parameters.AddWithValue(
                "@maxAttempts",
                maxAttempts);

            await expireLease.ExecuteNonQueryAsync(
                cancellationToken);
        }

        const string selectSql = """
            SELECT
                id,
                item_id,
                item_type,
                source_event_type,
                source_occurred_at,
                blob_url,
                attempts
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
            ORDER BY source_occurred_at, created_at, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """;

        Guid id = default;
        Guid itemId = default;
        ItemType itemType = default;
        ItemEventType sourceEventType = default;
        DateTime sourceOccurredAt = default;
        string blobUrl = string.Empty;
        var attempts = 0;
        var jobFound = false;

        await using (var select = new MySqlCommand(
                        selectSql,
                        connection,
                        transaction))
        {
            select.Parameters.AddWithValue(
                "@maxAttempts",
                maxAttempts);

            await using (var reader =
                        await select.ExecuteReaderAsync(
                            cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    jobFound = true;

                    // CHAR(36) UUID columns are returned as Guid
                    // by MySqlConnector.
                    id = reader.GetGuid(0);
                    itemId = reader.GetGuid(1);

                    itemType = reader.GetString(2) switch
                    {
                        "LOST" => ItemType.Lost,
                        "FOUND" => ItemType.Found,
                        _ => throw new InvalidDataException(
                            "Unsupported item type in image description.")
                    };

                    sourceEventType = reader.GetString(3) switch
                    {
                        "CREATED" => ItemEventType.Created,
                        "UPDATED" => ItemEventType.Updated,
                        _ => throw new InvalidDataException(
                            "Unsupported source event type.")
                    };

                    sourceOccurredAt = reader.GetDateTime(4);
                    blobUrl = reader.GetString(5);
                    attempts = reader.GetInt32(6) + 1;
                }
            }
        }

        // The reader has been disposed before the transaction is committed.
        if (!jobFound)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var leaseToken = Guid.NewGuid();

        const string claimSql = """
            UPDATE image_descriptions
            SET processing_status = 'PROCESSING',
                attempts = @attempts,
                lease_token = @leaseToken,
                lease_expires_at = TIMESTAMPADD(
                    SECOND,
                    @leaseSeconds,
                    UTC_TIMESTAMP(3)),
                next_retry_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @id
            AND attempts < @maxAttempts;
            """;

        await using (var claim = new MySqlCommand(
                        claimSql,
                        connection,
                        transaction))
        {
            claim.Parameters.AddWithValue("@id", id);
            claim.Parameters.AddWithValue("@attempts", attempts);
            claim.Parameters.AddWithValue(
                "@leaseToken",
                leaseToken);
            claim.Parameters.AddWithValue(
                "@leaseSeconds",
                leaseSeconds);
            claim.Parameters.AddWithValue(
                "@maxAttempts",
                maxAttempts);

            var claimed = await claim.ExecuteNonQueryAsync(
                cancellationToken);

            if (claimed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new ClaimedImageDescription(
            id,
            leaseToken,
            itemId,
            itemType,
            sourceEventType,
            sourceOccurredAt,
            blobUrl,
            attempts);
    }

    public async Task<bool> CompleteAsync(
        ClaimedImageDescription job,
        GeneratedImageDescription description,
        string modelName,
        CancellationToken cancellationToken)
    {
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

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        const string completeSql = """
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

        await using (var complete = new MySqlCommand(
                         completeSql,
                         connection,
                         transaction))
        {
            complete.Parameters.AddWithValue(
                "@id",
                job.Id);

            complete.Parameters.AddWithValue(
                "@leaseToken",
                job.LeaseToken);

            complete.Parameters.AddWithValue(
                "@description",
                description.Description);

            complete.Parameters.AddWithValue(
                "@attributesJson",
                attributesJson);

            complete.Parameters.AddWithValue(
                "@modelName",
                modelName);

            var completed = await complete.ExecuteNonQueryAsync(
                cancellationToken);

            if (completed != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        const string findNewerCurrentSql = """
            SELECT id
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @itemType
              AND processing_status = 'COMPLETED'
              AND is_superseded = 0
              AND source_occurred_at > @sourceOccurredAt
            ORDER BY source_occurred_at DESC, processed_at DESC
            LIMIT 1
            FOR UPDATE;
            """;

        Guid? newerDescriptionId = null;

        await using (var findNewer = new MySqlCommand(
                         findNewerCurrentSql,
                         connection,
                         transaction))
        {
            findNewer.Parameters.AddWithValue(
                "@itemId",
                job.ItemId);

            findNewer.Parameters.AddWithValue(
                "@itemType",
                job.ItemType.ToString().ToUpperInvariant());

            findNewer.Parameters.AddWithValue(
                "@sourceOccurredAt",
                job.SourceOccurredAt);

            var newerValue = await findNewer.ExecuteScalarAsync(
                cancellationToken);

            newerDescriptionId = ConvertDatabaseGuid(newerValue);
        }

        if (newerDescriptionId.HasValue)
        {
            const string supersedeCurrentSql = """
                UPDATE image_descriptions
                SET is_superseded = 1,
                    superseded_at = UTC_TIMESTAMP(3),
                    superseded_by_id = @newerDescriptionId,
                    updated_at = UTC_TIMESTAMP(3)
                WHERE id = @id
                  AND is_superseded = 0;
                """;

            await using var supersedeCurrent = new MySqlCommand(
                supersedeCurrentSql,
                connection,
                transaction);

            supersedeCurrent.Parameters.AddWithValue(
                "@id",
                job.Id);

            supersedeCurrent.Parameters.AddWithValue(
                "@newerDescriptionId",
                newerDescriptionId.Value);

            await supersedeCurrent.ExecuteNonQueryAsync(
                cancellationToken);
        }
        else if (job.SourceEventType == ItemEventType.Updated)
        {
            const string supersedePreviousSql = """
                UPDATE image_descriptions
                SET is_superseded = 1,
                    superseded_at = UTC_TIMESTAMP(3),
                    superseded_by_id = @newDescriptionId,
                    updated_at = UTC_TIMESTAMP(3)
                WHERE item_id = @itemId
                  AND item_type = @itemType
                  AND id <> @newDescriptionId
                  AND processing_status = 'COMPLETED'
                  AND is_superseded = 0
                  AND source_occurred_at <= @sourceOccurredAt;
                """;

            await using var supersedePrevious = new MySqlCommand(
                supersedePreviousSql,
                connection,
                transaction);

            supersedePrevious.Parameters.AddWithValue(
                "@itemId",
                job.ItemId);

            supersedePrevious.Parameters.AddWithValue(
                "@itemType",
                job.ItemType.ToString().ToUpperInvariant());

            supersedePrevious.Parameters.AddWithValue(
                "@newDescriptionId",
                job.Id);

            supersedePrevious.Parameters.AddWithValue(
                "@sourceOccurredAt",
                job.SourceOccurredAt);

            await supersedePrevious.ExecuteNonQueryAsync(
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
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

        command.Parameters.AddWithValue("@id", job.Id);

        command.Parameters.AddWithValue(
            "@leaseToken",
            job.LeaseToken);

        command.Parameters.AddWithValue(
            "@status",
            nextRetryAt.HasValue ? "PENDING" : "FAILED");

        command.Parameters.AddWithValue(
            "@errorCode",
            errorCode);

        command.Parameters.AddWithValue(
            "@nextRetryAt",
            (object?)nextRetryAt ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(
            cancellationToken) == 1;
    }

    public async Task<CurrentImageDescription?> GetLatestCurrentCompletedAsync(
        Guid itemId,
        ItemType itemType,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                item_id,
                item_type,
                description,
                attributes_json,
                source_occurred_at,
                processed_at
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @itemType
              AND processing_status = 'COMPLETED'
              AND is_superseded = 0
            ORDER BY source_occurred_at DESC, processed_at DESC
            LIMIT 1;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@itemId",
            itemId);

        command.Parameters.AddWithValue(
            "@itemType",
            itemType.ToString().ToUpperInvariant());

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CurrentImageDescription(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2) == "LOST"
                ? ItemType.Lost
                : ItemType.Found,
            reader.GetString(3),
            reader.IsDBNull(4)
                ? null
                : reader.GetString(4),
            reader.GetDateTime(5),
            reader.GetDateTime(6));
    }

    private static Guid? ConvertDatabaseGuid(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        if (value is Guid guid)
        {
            return guid;
        }

        if (value is string text &&
            Guid.TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            "The database returned an invalid description ID.");
    }
}