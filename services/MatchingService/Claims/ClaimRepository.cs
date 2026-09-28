using System.Data;
using System.Text.Json;
using MatchingService.Databases;
using MatchingService.Lifecycle;
using MatchingService.Services;
using MySqlConnector;

namespace MatchingService.Claims;

public sealed class ClaimRepository(
    IDbConnectionFactory connections,
    BlobUrlPhotoKeyGenerator photoKeys)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<bool> PairExistsAsync(
        Guid lostItemId,
        Guid foundItemId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        const string inactiveSql = """
            SELECT 1
            FROM matching_item_states
            WHERE inactive_reason IS NOT NULL
              AND (
                  (item_type = 'LOST' AND item_id = @lostItemId)
                  OR
                  (item_type = 'FOUND' AND item_id = @foundItemId)
              )
            LIMIT 1;
            """;

        await using (var inactive = new MySqlCommand(inactiveSql, connection))
        {
            inactive.Parameters.AddWithValue("@lostItemId", lostItemId);
            inactive.Parameters.AddWithValue("@foundItemId", foundItemId);

            if (await inactive.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new ClaimException(
                    409, "One of these reports has been resolved or deleted.");
            }
        }

        const string sql = """
            SELECT 1
            FROM matches
            WHERE (lost_item_id = @lostItemId AND found_item_id = @foundItemId)
               OR (status = 'CONFIRMED' AND is_active = 1
                   AND (lost_item_id = @lostItemId OR found_item_id = @foundItemId))
            LIMIT 1;
            """;

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<ImageEvidence> GetImageEvidenceAsync(
        ItemReport report,
        string itemType,
        CancellationToken cancellationToken)
    {
        var urls = (report.PhotoUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count != 1)
        {
            throw new ClaimException(
                409, "Each report must have one current photo before image comparison can be used.");
        }

        string photoKey;

        try
        {
            (_, photoKey) = photoKeys.Create(urls[0]);
        }
        catch (InvalidDataException)
        {
            throw new ClaimException(
                409, "A report photo is not ready for matching.");
        }

        const string sql = """
            SELECT id, processing_status, description, attributes_json
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @itemType
              AND photo_key = @photoKey
              AND is_superseded = 0
            LIMIT 1;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@itemId", report.Id);
        command.Parameters.AddWithValue("@itemType", itemType);
        command.Parameters.AddWithValue("@photoKey", photoKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ClaimException(
                409, "The current photos are still being analysed. Please try again shortly.");
        }

        var status = reader.GetString(1).ToUpperInvariant();

        if (status is "PENDING" or "PROCESSING")
        {
            throw new ClaimException(
                409, "The current photos are still being analysed. Please try again shortly.");
        }

        if (status != "COMPLETED")
        {
            throw new ClaimException(
                409, "A current photo could not be analysed. Please try again later.");
        }

        if (reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
        {
            throw new ClaimException(
                409, "The current photos are still being analysed. Please try again shortly.");
        }

        return new ImageEvidence(
            reader.GetGuid(0),
            reader.GetString(2),
            reader.IsDBNull(3) ? "{}" : reader.GetString(3));
    }

    public async Task<MatchView> CreateAsync(
        VerifiedPair pair,
        ClaimPreview preview,
        Guid claimantId,
        CancellationToken cancellationToken,
        string? finderEmail = null,
        string? finderPhone = null,
        string? lostReporterEmail = null,
        string? lostReporterPhone = null)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var status = pair.ClaimantRole == "LOST"
            ? "LOST_REPORTER_CONFIRMED"
            : "FINDER_CONFIRMED";

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            await ItemLifecycleRepository.EnsurePairActiveAsync(
                connection, transaction,
                pair.Lost.Id, pair.Found.Id, cancellationToken);

            const string sql = """
                INSERT INTO matches (
                    id, lost_item_id, found_item_id,
                    lost_reporter_id, finder_id, claimant_id, claimant_role,
                    status, confidence_score, scoring_version,
                    lost_snapshot, found_snapshot,
                    finder_email, finder_phone,
                    lost_reporter_email, lost_reporter_phone,
                    created_at, updated_at
                )
                VALUES (
                    @id, @lostItemId, @foundItemId,
                    @lostReporterId, @finderId, @claimantId, @claimantRole,
                    @status, @score, @scoringVersion,
                    @lostSnapshot, @foundSnapshot,
                    @finderEmail, @finderPhone,
                    @lostReporterEmail, @lostReporterPhone,
                    @now, @now
                );
                """;

            await using var command = new MySqlCommand(sql, connection, transaction);

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@lostItemId", pair.Lost.Id);
            command.Parameters.AddWithValue("@foundItemId", pair.Found.Id);
            command.Parameters.AddWithValue("@lostReporterId", pair.Lost.UserId);
            command.Parameters.AddWithValue("@finderId", pair.Found.UserId);
            command.Parameters.AddWithValue("@claimantId", claimantId);
            command.Parameters.AddWithValue("@claimantRole", pair.ClaimantRole);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@score", preview.Score);
            command.Parameters.AddWithValue("@scoringVersion", ClaimService.ScoringVersion);
            command.Parameters.AddWithValue(
                "@lostSnapshot", JsonSerializer.Serialize(preview.Lost, JsonOptions));
            command.Parameters.AddWithValue(
                "@foundSnapshot", JsonSerializer.Serialize(preview.Found, JsonOptions));
            command.Parameters.AddWithValue("@finderEmail", DbText(finderEmail));
            command.Parameters.AddWithValue("@finderPhone", DbText(finderPhone));
            command.Parameters.AddWithValue("@lostReporterEmail", DbText(lostReporterEmail));
            command.Parameters.AddWithValue("@lostReporterPhone", DbText(lostReporterPhone));
            command.Parameters.AddWithValue("@now", now);

            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new ClaimException(
                409, "A match already exists for this lost and found pair.");
        }
        catch (MySqlException exception) when (exception.Number is 1205 or 1213)
        {
            throw new ClaimException(
                409, "A report is being updated. Refresh and try again.");
        }

        return new MatchView(
            id, status, pair.ClaimantRole, preview.Score,
            now, preview.Lost, preview.Found);
    }

    public async Task<List<MatchView>> GetMineAsync(
        Guid userId,
        int page,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, status, claimant_role, confidence_score,
                   created_at, lost_snapshot, found_snapshot
            FROM matches
            WHERE is_active = 1
              AND (lost_reporter_id = @userId OR finder_id = @userId)
              AND status IN (
                  'LOST_REPORTER_CONFIRMED', 'FINDER_CONFIRMED',
                  'CONFIRMED', 'REJECTED'
              )
            ORDER BY created_at DESC, id DESC
            LIMIT 20 OFFSET @offset;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@offset", ((long)page - 1) * 20);

        var matches = new List<MatchView>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new MatchView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                ReadSnapshot(reader.GetString(5)),
                ReadSnapshot(reader.GetString(6))));
        }

        return matches;
    }

    private static object DbText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static ClaimItemView ReadSnapshot(string json) =>
        JsonSerializer.Deserialize<ClaimItemView>(json, JsonOptions)
        ?? throw new InvalidDataException("A match contains an invalid item snapshot.");
}