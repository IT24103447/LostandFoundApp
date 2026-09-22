using System.Text.Json;
using MatchingService.Databases;
using MatchingService.Services;
using MySqlConnector;

namespace MatchingService.Claims;

public sealed class ClaimRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IDbConnectionFactory _connections;
    private readonly BlobUrlPhotoKeyGenerator _photoKeys;

    public ClaimRepository(
        IDbConnectionFactory connections,
        BlobUrlPhotoKeyGenerator photoKeys)
    {
        _connections = connections;
        _photoKeys = photoKeys;
    }

    public async Task<bool> PairExistsAsync(
        Guid lostItemId,
        Guid foundItemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM matches
            WHERE lost_item_id = @lostItemId
              AND found_item_id = @foundItemId
            LIMIT 1;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@lostItemId",
            lostItemId);

        command.Parameters.AddWithValue(
            "@foundItemId",
            foundItemId);

        return await command.ExecuteScalarAsync(
            cancellationToken) is not null;
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
                StatusCodes.Status409Conflict,
                "Each report must have one current photo " +
                "before image comparison can be used.");
        }

        string photoKey;

        try
        {
            (_, photoKey) = _photoKeys.Create(urls[0]);
        }
        catch (InvalidDataException)
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "A report photo is not ready for matching.");
        }

        const string sql = """
            SELECT
                id,
                processing_status,
                description,
                attributes_json
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @itemType
              AND photo_key = @photoKey
              AND is_superseded = 0
            LIMIT 1;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@itemId",
            report.Id);

        command.Parameters.AddWithValue(
            "@itemType",
            itemType);

        command.Parameters.AddWithValue(
            "@photoKey",
            photoKey);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "The current photos are still being analysed. " +
                "Please try again shortly.");
        }

        var processingStatus = reader.GetString(1);

        if (string.Equals(
                processingStatus,
                "PENDING",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                processingStatus,
                "PROCESSING",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "The current photos are still being analysed. " +
                "Please try again shortly.");
        }

        if (!string.Equals(
                processingStatus,
                "COMPLETED",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "A current photo could not be analysed. " +
                "Please try again later.");
        }

        if (reader.IsDBNull(2) ||
            string.IsNullOrWhiteSpace(reader.GetString(2)))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "The current photos are still being analysed. " +
                "Please try again shortly.");
        }

        return new ImageEvidence(
            reader.GetGuid(0),
            reader.GetString(2),
            reader.IsDBNull(3)
                ? "{}"
                : reader.GetString(3));
    }

    public async Task<MatchView> CreateAsync(
        VerifiedPair pair,
        ClaimPreview preview,
        Guid claimantId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var status = pair.ClaimantRole == "LOST"
            ? "LOST_REPORTER_CONFIRMED"
            : "FINDER_CONFIRMED";

        const string sql = """
            INSERT INTO matches (
                id,
                lost_item_id,
                found_item_id,
                lost_reporter_id,
                finder_id,
                claimant_id,
                claimant_role,
                status,
                confidence_score,
                scoring_version,
                lost_snapshot,
                found_snapshot,
                created_at,
                updated_at
            )
            VALUES (
                @id,
                @lostItemId,
                @foundItemId,
                @lostReporterId,
                @finderId,
                @claimantId,
                @claimantRole,
                @status,
                @score,
                @scoringVersion,
                @lostSnapshot,
                @foundSnapshot,
                @now,
                @now
            );
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", id);

        command.Parameters.AddWithValue(
            "@lostItemId",
            pair.Lost.Id);

        command.Parameters.AddWithValue(
            "@foundItemId",
            pair.Found.Id);

        command.Parameters.AddWithValue(
            "@lostReporterId",
            pair.Lost.UserId);

        command.Parameters.AddWithValue(
            "@finderId",
            pair.Found.UserId);

        command.Parameters.AddWithValue(
            "@claimantId",
            claimantId);

        command.Parameters.AddWithValue(
            "@claimantRole",
            pair.ClaimantRole);

        command.Parameters.AddWithValue(
            "@status",
            status);

        command.Parameters.AddWithValue(
            "@score",
            preview.Score);

        command.Parameters.AddWithValue(
            "@scoringVersion",
            ClaimService.ScoringVersion);

        command.Parameters.AddWithValue(
            "@lostSnapshot",
            JsonSerializer.Serialize(
                preview.Lost,
                JsonOptions));

        command.Parameters.AddWithValue(
            "@foundSnapshot",
            JsonSerializer.Serialize(
                preview.Found,
                JsonOptions));

        command.Parameters.AddWithValue("@now", now);

        try
        {
            await command.ExecuteNonQueryAsync(
                cancellationToken);
        }
        catch (MySqlException exception)
            when (exception.Number == 1062)
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "A match already exists for this lost and found pair.");
        }

        return new MatchView(
            id,
            status,
            pair.ClaimantRole,
            preview.Score,
            now,
            preview.Lost,
            preview.Found);
    }

    public async Task<List<MatchView>> GetMineAsync(
        Guid userId,
        int page,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                status,
                claimant_role,
                confidence_score,
                created_at,
                lost_snapshot,
                found_snapshot
            FROM matches
            WHERE is_active = 1
                AND (
                    lost_reporter_id = @userId
                    OR finder_id = @userId
                )
                AND status IN (
                    'LOST_REPORTER_CONFIRMED',
                    'FINDER_CONFIRMED',
                    'CONFIRMED',
                    'REJECTED'
                )
            ORDER BY created_at DESC, id DESC
            LIMIT 20 OFFSET @offset;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@userId",
            userId);

        command.Parameters.AddWithValue(
            "@offset",
            (page - 1) * 20);

        var matches = new List<MatchView>();

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var lost = JsonSerializer.Deserialize<ClaimItemView>(
                reader.GetString(5),
                JsonOptions);

            var found = JsonSerializer.Deserialize<ClaimItemView>(
                reader.GetString(6),
                JsonOptions);

            if (lost is null || found is null)
            {
                throw new InvalidDataException(
                    "A match contains an invalid item snapshot.");
            }

            matches.Add(new MatchView(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDecimal(3),
                DateTime.SpecifyKind(
                    reader.GetDateTime(4),
                    DateTimeKind.Utc),
                lost,
                found));
        }

        return matches;
    }
}