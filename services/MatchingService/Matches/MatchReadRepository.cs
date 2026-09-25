using System.Data;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Matches;

public sealed class MatchReadRepository(
    IDbConnectionFactory connections) : IMatchReadRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const string Columns = """
        id, lost_reporter_id, finder_id, claimant_id, status,
        is_active, confidence_score, created_at,
        lost_snapshot, found_snapshot,
        deactivated_at, deactivation_reason,
        deactivated_item_id, deactivated_item_type
        """;

    // Null reasons are retained for older deactivations. The UI shows a
    // generic explanation rather than inventing a reason or timestamp.
    private const string DeactivatedFilter = """
        is_active = 0
        AND (
            deactivation_reason IN (
                'ITEM_DELETED',
                'ITEM_RESOLVED',
                'MATCH_CONFIRMED_ELSEWHERE'
            )
            OR deactivation_reason IS NULL
        )
        AND status IN (
            'AWAITING_CLAIMANT_CONFIRMATION',
            'LOST_REPORTER_CONFIRMED',
            'FINDER_CONFIRMED'
        )
        """;

    private const string ActiveVisibleFilter = """
        is_active = 1
        AND status IN (
            'LOST_REPORTER_CONFIRMED',
            'FINDER_CONFIRMED',
            'CONFIRMED',
            'REJECTED'
        )
        """;

    public async Task<StoredMatchPage> GetPageAsync(
        Guid userId,
        string section,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var filter = $"""
            (lost_reporter_id = @userId OR finder_id = @userId)
            AND ({GetSectionFilter(section)})
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);

        long total;

        await using (var count = new MySqlCommand(
            $"SELECT COUNT(*) FROM matches WHERE {filter};",
            connection, transaction))
        {
            count.Parameters.AddWithValue("@userId", userId);

            total = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        var sql = $"""
            SELECT {Columns}
            FROM matches
            WHERE {filter}
            ORDER BY created_at DESC, id DESC
            LIMIT @size OFFSET @offset;
            """;

        var items = new List<StoredMatch>();

        await using (var command = new MySqlCommand(
            sql, connection, transaction))
        {
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@size", size);
            command.Parameters.AddWithValue("@offset", ((long)page - 1) * size);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMatch(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new StoredMatchPage(items, total);
    }

    public async Task<StoredMatch?> GetByIdAsync(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(
            $"SELECT {Columns} FROM matches WHERE id = @id LIMIT 1;",
            connection);

        command.Parameters.AddWithValue("@id", matchId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadMatch(reader)
            : null;
    }

    private static string GetSectionFilter(string section) => section switch
    {
        "active" => """
            is_active = 1
            AND status IN ('LOST_REPORTER_CONFIRMED', 'FINDER_CONFIRMED')
            """,

        "waiting-on-you" => """
            is_active = 1 AND (
                (status = 'FINDER_CONFIRMED' AND lost_reporter_id = @userId)
                OR
                (status = 'LOST_REPORTER_CONFIRMED' AND finder_id = @userId)
            )
            """,

        "waiting-on-other" => """
            is_active = 1 AND (
                (status = 'LOST_REPORTER_CONFIRMED' AND lost_reporter_id = @userId)
                OR
                (status = 'FINDER_CONFIRMED' AND finder_id = @userId)
            )
            """,

        "confirmed" => "is_active = 1 AND status = 'CONFIRMED'",
        "rejected" => "is_active = 1 AND status = 'REJECTED'",
        "deactivated" => DeactivatedFilter,

        "closed" => $"""
            (is_active = 1 AND status IN ('CONFIRMED', 'REJECTED'))
            OR ({DeactivatedFilter})
            """,

        "all" => $"({ActiveVisibleFilter}) OR ({DeactivatedFilter})",

        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    private static StoredMatch ReadMatch(MySqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetDecimal(6),
            Utc(reader.GetDateTime(7)),
            ReadSnapshot(reader.GetString(8)),
            ReadSnapshot(reader.GetString(9)))
        {
            DeactivatedAt = reader.IsDBNull(10)
                ? null : Utc(reader.GetDateTime(10)),
            DeactivationReason = reader.IsDBNull(11)
                ? null : reader.GetString(11),
            DeactivatedItemId = reader.IsDBNull(12)
                ? null : reader.GetGuid(12),
            DeactivatedItemType = reader.IsDBNull(13)
                ? null : reader.GetString(13)
        };

    private static DateTime Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static ClaimItemView ReadSnapshot(string json) =>
        JsonSerializer.Deserialize<ClaimItemView>(json, JsonOptions)
        ?? throw new InvalidDataException(
            "A match contains an invalid item snapshot.");
}