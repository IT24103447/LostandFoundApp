using System.Data;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Databases;
using MatchingService.Services;
using MySqlConnector;

namespace MatchingService.Reevaluation;

public sealed record MatchReevaluationJob(
    Guid EventId,
    string ItemType,
    Guid ItemId,
    Guid LeaseToken,
    int Attempts,
    DateTime CreatedAt);

public sealed record MatchReevaluationPair(
    Guid MatchId,
    Guid LostItemId,
    Guid FoundItemId,
    Guid LostReporterId,
    Guid FinderId,
    string LostSnapshotJson,
    string FoundSnapshotJson);

public enum PhotoReadiness
{
    Ready,
    Pending,
    Failed
}

public sealed class MatchReevaluationRepository(
    IDbConnectionFactory connections,
    BlobUrlPhotoKeyGenerator photoKeys)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task RecordEventAsync(
        MatchItemSnapshotEvent itemEvent,
        CancellationToken cancellationToken)
    {
        var snapshotJson = JsonSerializer.Serialize(
            itemEvent.Report,
            JsonOptions);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        const string insertSql = """
            INSERT IGNORE INTO matching_item_snapshots (
                item_type, item_id, source_event_id,
                source_event_type, source_occurred_at,
                snapshot_json, updated_at
            )
            VALUES (
                @type, @itemId, @eventId,
                @eventType, @occurredAt,
                @snapshot, UTC_TIMESTAMP(3)
            );
            """;

        int inserted;

        await using (var insert = new MySqlCommand(
            insertSql,
            connection,
            transaction))
        {
            AddSnapshotParameters(
                insert,
                itemEvent,
                snapshotJson);

            inserted = await insert.ExecuteNonQueryAsync(
                cancellationToken);
        }

        if (inserted == 0)
        {
            const string currentSql = """
                SELECT source_event_id, source_occurred_at
                FROM matching_item_snapshots
                WHERE item_type = @type
                  AND item_id = @itemId
                FOR UPDATE;
                """;

            Guid currentEventId;
            DateTime currentOccurredAt;

            await using (var current = new MySqlCommand(
                currentSql,
                connection,
                transaction))
            {
                current.Parameters.AddWithValue(
                    "@type",
                    itemEvent.ItemType);

                current.Parameters.AddWithValue(
                    "@itemId",
                    itemEvent.ItemId);

                await using var reader =
                    await current.ExecuteReaderAsync(
                        cancellationToken);

                if (!await reader.ReadAsync(
                    cancellationToken))
                {
                    throw new InvalidDataException(
                        "The item snapshot disappeared.");
                }

                currentEventId = reader.GetGuid(0);
                currentOccurredAt = reader.GetDateTime(1);
            }

            // Kafka delivery can repeat or arrive after a newer update.
            if (currentEventId == itemEvent.EventId ||
                currentOccurredAt > itemEvent.OccurredAt ||
                (currentOccurredAt == itemEvent.OccurredAt &&
                 string.CompareOrdinal(
                     currentEventId.ToString(),
                     itemEvent.EventId.ToString()) >= 0))
            {
                await transaction.CommitAsync(
                    cancellationToken);

                return;
            }

            const string updateSql = """
                UPDATE matching_item_snapshots
                SET source_event_id = @eventId,
                    source_event_type = @eventType,
                    source_occurred_at = @occurredAt,
                    snapshot_json = @snapshot,
                    updated_at = UTC_TIMESTAMP(3)
                WHERE item_type = @type
                  AND item_id = @itemId;
                """;

            await using var update = new MySqlCommand(
                updateSql,
                connection,
                transaction);

            AddSnapshotParameters(
                update,
                itemEvent,
                snapshotJson);

            await update.ExecuteNonQueryAsync(
                cancellationToken);
        }

        if (itemEvent.IsUpdate)
        {
            const string jobSql = """
                INSERT IGNORE INTO match_reevaluation_jobs (
                    event_id, item_type, item_id, status,
                    next_attempt_at, created_at, updated_at
                )
                VALUES (
                    @eventId, @type, @itemId, 'PENDING',
                    UTC_TIMESTAMP(3),
                    UTC_TIMESTAMP(3),
                    UTC_TIMESTAMP(3)
                );
                """;

            await using var job = new MySqlCommand(
                jobSql,
                connection,
                transaction);

            job.Parameters.AddWithValue(
                "@eventId",
                itemEvent.EventId);

            job.Parameters.AddWithValue(
                "@type",
                itemEvent.ItemType);

            job.Parameters.AddWithValue(
                "@itemId",
                itemEvent.ItemId);

            await job.ExecuteNonQueryAsync(
                cancellationToken);
        }

        await transaction.CommitAsync(
            cancellationToken);
    }

    public async Task<MatchReevaluationJob?> ClaimNextAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        const string exhaustedSql = """
            UPDATE match_reevaluation_jobs
            SET status = 'FAILED',
                error_code = 'WORKER_LEASE_EXPIRED',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE status = 'PROCESSING'
              AND lease_expires_at <= UTC_TIMESTAMP(3)
              AND attempts >= 5;
            """;

        await using (var exhausted = new MySqlCommand(
            exhaustedSql,
            connection,
            transaction))
        {
            await exhausted.ExecuteNonQueryAsync(
                cancellationToken);
        }

        const string selectSql = """
            SELECT event_id, item_type, item_id,
                   attempts, created_at
            FROM match_reevaluation_jobs
            WHERE attempts < 5
              AND (
                  (status = 'PENDING'
                   AND next_attempt_at <= UTC_TIMESTAMP(3))
                  OR
                  (status = 'PROCESSING'
                   AND lease_expires_at <= UTC_TIMESTAMP(3))
              )
            ORDER BY created_at, event_id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """;

        Guid eventId = default;
        Guid itemId = default;
        string itemType = string.Empty;
        int attempts = 0;
        DateTime createdAt = default;
        bool found = false;

        await using (var select = new MySqlCommand(
            selectSql,
            connection,
            transaction))
        {
            await using var reader =
                await select.ExecuteReaderAsync(
                    cancellationToken);

            if (await reader.ReadAsync(
                cancellationToken))
            {
                found = true;
                eventId = reader.GetGuid(0);
                itemType = reader.GetString(1);
                itemId = reader.GetGuid(2);
                attempts = reader.GetInt32(3) + 1;
                createdAt = DateTime.SpecifyKind(
                    reader.GetDateTime(4),
                    DateTimeKind.Utc);
            }
        }

        if (!found)
        {
            await transaction.CommitAsync(
                cancellationToken);

            return null;
        }

        var leaseToken = Guid.NewGuid();

        const string claimSql = """
            UPDATE match_reevaluation_jobs
            SET status = 'PROCESSING',
                attempts = @attempts,
                lease_token = @leaseToken,
                lease_expires_at = TIMESTAMPADD(
                    MINUTE, 5, UTC_TIMESTAMP(3)),
                next_attempt_at = NULL,
                error_code = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE event_id = @eventId;
            """;

        await using (var claim = new MySqlCommand(
            claimSql,
            connection,
            transaction))
        {
            claim.Parameters.AddWithValue(
                "@eventId",
                eventId);

            claim.Parameters.AddWithValue(
                "@attempts",
                attempts);

            claim.Parameters.AddWithValue(
                "@leaseToken",
                leaseToken);

            await claim.ExecuteNonQueryAsync(
                cancellationToken);
        }

        await transaction.CommitAsync(
            cancellationToken);

        return new MatchReevaluationJob(
            eventId,
            itemType,
            itemId,
            leaseToken,
            attempts,
            createdAt);
    }

    public async Task<bool> IsCurrentAsync(
        MatchReevaluationJob job,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_event_id
            FROM matching_item_snapshots
            WHERE item_type = @type
              AND item_id = @itemId;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@type",
            job.ItemType);

        command.Parameters.AddWithValue(
            "@itemId",
            job.ItemId);

        var value = await command.ExecuteScalarAsync(
            cancellationToken);

        return value is Guid eventId &&
            eventId == job.EventId;
    }

    public async Task<List<MatchReevaluationPair>>
        GetActivePairsAsync(
            MatchReevaluationJob job,
            CancellationToken cancellationToken)
    {
        var itemColumn = job.ItemType == "LOST"
            ? "lost_item_id"
            : "found_item_id";

        var sql = $"""
            SELECT id, lost_item_id, found_item_id,
                   lost_reporter_id, finder_id,
                   lost_snapshot, found_snapshot
            FROM matches
            WHERE {itemColumn} = @itemId
              AND is_active = 1
              AND status IN (
                  'LOST_REPORTER_CONFIRMED',
                  'FINDER_CONFIRMED'
              )
            ORDER BY id;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@itemId",
            job.ItemId);

        var pairs = new List<MatchReevaluationPair>();

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
            cancellationToken))
        {
            pairs.Add(new MatchReevaluationPair(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return pairs;
    }

    public async Task<ItemReport> GetLatestReportAsync(
        string type,
        Guid itemId,
        Guid ownerId,
        string fallbackJson,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_event_type, snapshot_json
            FROM matching_item_snapshots
            WHERE item_type = @type
              AND item_id = @itemId;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@type",
            type);

        command.Parameters.AddWithValue(
            "@itemId",
            itemId);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (await reader.ReadAsync(
            cancellationToken) &&
            reader.GetString(0) == "UPDATED")
        {
            var report =
                JsonSerializer.Deserialize<ItemReport>(
                    reader.GetString(1),
                    JsonOptions);

            if (report is null ||
                report.Id != itemId ||
                report.UserId != ownerId)
            {
                throw new InvalidDataException(
                    "An item snapshot is invalid.");
            }

            return report;
        }

        // Existing claims may predate this new snapshot table.
        var view =
            JsonSerializer.Deserialize<ClaimItemView>(
                fallbackJson,
                JsonOptions)
            ?? throw new InvalidDataException(
                "A match snapshot is invalid.");

        if (view.Id != itemId)
        {
            throw new InvalidDataException(
                "A match snapshot belongs to another item.");
        }

        return new ItemReport
        {
            Id = itemId,
            UserId = ownerId,
            Title = view.Title,
            Category = view.Category,
            Description = view.Description,
            Status = "ACTIVE",
            DateLost = type == "LOST"
                ? view.Date
                : string.Empty,
            DateFound = type == "FOUND"
                ? view.Date
                : string.Empty,
            LastKnownLocation = type == "LOST"
                ? view.Location
                : string.Empty,
            LocationFound = type == "FOUND"
                ? view.Location
                : string.Empty,
            PhotoUrls = string.IsNullOrWhiteSpace(
                view.PhotoUrl)
                    ? []
                    : [view.PhotoUrl]
        };
    }

    public async Task<PhotoReadiness>
        GetPhotoReadinessAsync(
            ItemReport report,
            string type,
            CancellationToken cancellationToken)
    {
        var urls = (report.PhotoUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count == 0)
        {
            return PhotoReadiness.Ready;
        }

        if (urls.Count != 1)
        {
            return PhotoReadiness.Failed;
        }

        string photoKey;

        try
        {
            (_, photoKey) = photoKeys.Create(
                urls[0]);
        }
        catch (InvalidDataException)
        {
            return PhotoReadiness.Failed;
        }

        const string sql = """
            SELECT processing_status, description
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @type
              AND photo_key = @photoKey
              AND is_superseded = 0
            LIMIT 1;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@itemId",
            report.Id);

        command.Parameters.AddWithValue(
            "@type",
            type);

        command.Parameters.AddWithValue(
            "@photoKey",
            photoKey);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
            cancellationToken))
        {
            return PhotoReadiness.Pending;
        }

        var status = reader.GetString(0);

        return status switch
        {
            "COMPLETED" when
                !reader.IsDBNull(1) &&
                !string.IsNullOrWhiteSpace(
                    reader.GetString(1)) =>
                PhotoReadiness.Ready,

            "FAILED" => PhotoReadiness.Failed,

            _ => PhotoReadiness.Pending
        };
    }

    public async Task ApplyScoreAsync(
        MatchReevaluationJob job,
        MatchReevaluationPair pair,
        ItemReport lost,
        ItemReport found,
        decimal score,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        const string leaseSql = """
            SELECT lease_token
            FROM match_reevaluation_jobs
            WHERE event_id = @eventId
              AND status = 'PROCESSING'
            FOR UPDATE;
            """;

        Guid? currentLease;

        await using (var lease = new MySqlCommand(
            leaseSql,
            connection,
            transaction))
        {
            lease.Parameters.AddWithValue(
                "@eventId",
                job.EventId);

            var value = await lease.ExecuteScalarAsync(
                cancellationToken);

            currentLease = value is Guid token
                ? token
                : null;
        }

        if (currentLease != job.LeaseToken)
        {
            await transaction.CommitAsync(
                cancellationToken);

            return;
        }

        const string currentSql = """
            SELECT source_event_id
            FROM matching_item_snapshots
            WHERE item_type = @type
              AND item_id = @itemId
            FOR UPDATE;
            """;

        Guid? currentEventId;

        await using (var current = new MySqlCommand(
            currentSql,
            connection,
            transaction))
        {
            current.Parameters.AddWithValue(
                "@type",
                job.ItemType);

            current.Parameters.AddWithValue(
                "@itemId",
                job.ItemId);

            var value =
                await current.ExecuteScalarAsync(
                    cancellationToken);

            currentEventId = value is Guid eventId
                ? eventId
                : null;
        }

        if (currentEventId != job.EventId)
        {
            await transaction.CommitAsync(
                cancellationToken);

            return;
        }

        const string updateSql = """
            UPDATE matches
            SET confidence_score = @score,
                scoring_version = @scoringVersion,
                lost_snapshot = @lostSnapshot,
                found_snapshot = @foundSnapshot,
                is_active = CASE
                    WHEN @belowThreshold = 1
                    THEN 0
                    ELSE is_active
                END,
                deactivated_at = CASE
                    WHEN @belowThreshold = 1
                    THEN UTC_TIMESTAMP(3)
                    ELSE deactivated_at
                END,
                deactivation_reason = CASE
                    WHEN @belowThreshold = 1
                    THEN 'ITEM_UPDATED_NO_LONGER_MATCHES'
                    ELSE deactivation_reason
                END,
                deactivated_item_id = CASE
                    WHEN @belowThreshold = 1
                    THEN @itemId
                    ELSE deactivated_item_id
                END,
                deactivated_item_type = CASE
                    WHEN @belowThreshold = 1
                    THEN @type
                    ELSE deactivated_item_type
                END,
                updated_at = UTC_TIMESTAMP(3)
            WHERE id = @matchId
              AND is_active = 1
              AND status IN (
                  'LOST_REPORTER_CONFIRMED',
                  'FINDER_CONFIRMED'
              );
            """;

        var belowThreshold =
            score < ClaimService.Threshold;

        int changed;

        await using (var update = new MySqlCommand(
            updateSql,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(
                "@matchId",
                pair.MatchId);

            update.Parameters.AddWithValue(
                "@score",
                score);

            update.Parameters.AddWithValue(
                "@scoringVersion",
                ClaimService.ScoringVersion);

            update.Parameters.AddWithValue(
                "@lostSnapshot",
                JsonSerializer.Serialize(
                    lost.ToView("LOST"),
                    JsonOptions));

            update.Parameters.AddWithValue(
                "@foundSnapshot",
                JsonSerializer.Serialize(
                    found.ToView("FOUND"),
                    JsonOptions));

            update.Parameters.AddWithValue(
                "@belowThreshold",
                belowThreshold ? 1 : 0);

            update.Parameters.AddWithValue(
                "@itemId",
                job.ItemId);

            update.Parameters.AddWithValue(
                "@type",
                job.ItemType);

            changed = await update.ExecuteNonQueryAsync(
                cancellationToken);
        }

        if (changed == 1 && belowThreshold)
        {
            const string cancelSql = """
                UPDATE match_notifications
                SET status = 'CANCELLED',
                    next_attempt_at = NULL,
                    lease_token = NULL,
                    lease_expires_at = NULL,
                    error_code = 'MATCH_INACTIVE',
                    updated_at = UTC_TIMESTAMP(3)
                WHERE match_id = @matchId
                  AND status IN ('PENDING', 'FAILED');
                """;

            await using var cancel = new MySqlCommand(
                cancelSql,
                connection,
                transaction);

            cancel.Parameters.AddWithValue(
                "@matchId",
                pair.MatchId);

            await cancel.ExecuteNonQueryAsync(
                cancellationToken);
        }

        await transaction.CommitAsync(
            cancellationToken);
    }

    public Task DeferAsync(
        MatchReevaluationJob job,
        CancellationToken cancellationToken) =>
        ChangeJobAsync(
            job,
            "PENDING",
            "IMAGE_DESCRIPTION_PENDING",
            15,
            restoreAttempt: true,
            cancellationToken);

    public Task FailAsync(
        MatchReevaluationJob job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken) =>
        ChangeJobAsync(
            job,
            retryable && job.Attempts < 5
                ? "PENDING"
                : "FAILED",
            errorCode,
            retryable && job.Attempts < 5
                ? 30
                : null,
            restoreAttempt: false,
            cancellationToken);

    public Task CompleteAsync(
        MatchReevaluationJob job,
        CancellationToken cancellationToken) =>
        ChangeJobAsync(
            job,
            "COMPLETED",
            null,
            null,
            restoreAttempt: false,
            cancellationToken);

    private async Task ChangeJobAsync(
        MatchReevaluationJob job,
        string status,
        string? errorCode,
        int? retryAfterSeconds,
        bool restoreAttempt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE match_reevaluation_jobs
            SET status = @status,
                error_code = @errorCode,
                attempts = CASE
                    WHEN @restoreAttempt = 1
                    THEN GREATEST(0, attempts - 1)
                    ELSE attempts
                END,
                next_attempt_at = CASE
                    WHEN @retrySeconds IS NULL
                    THEN NULL
                    ELSE TIMESTAMPADD(
                        SECOND,
                        @retrySeconds,
                        UTC_TIMESTAMP(3))
                END,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = UTC_TIMESTAMP(3)
            WHERE event_id = @eventId
              AND status = 'PROCESSING'
              AND lease_token = @leaseToken;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@eventId",
            job.EventId);

        command.Parameters.AddWithValue(
            "@leaseToken",
            job.LeaseToken);

        command.Parameters.AddWithValue(
            "@status",
            status);

        command.Parameters.AddWithValue(
            "@errorCode",
            (object?)errorCode ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "@retrySeconds",
            (object?)retryAfterSeconds ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "@restoreAttempt",
            restoreAttempt ? 1 : 0);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static void AddSnapshotParameters(
        MySqlCommand command,
        MatchItemSnapshotEvent itemEvent,
        string snapshotJson)
    {
        command.Parameters.AddWithValue(
            "@type",
            itemEvent.ItemType);

        command.Parameters.AddWithValue(
            "@itemId",
            itemEvent.ItemId);

        command.Parameters.AddWithValue(
            "@eventId",
            itemEvent.EventId);

        command.Parameters.AddWithValue(
            "@eventType",
            itemEvent.IsUpdate
                ? "UPDATED"
                : "CREATED");

        command.Parameters.AddWithValue(
            "@occurredAt",
            itemEvent.OccurredAt);

        command.Parameters.AddWithValue(
            "@snapshot",
            snapshotJson);
    }
}