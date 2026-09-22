using System.Data;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Matches;

public sealed class MatchReadRepository : IMatchReadRepository
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const string Columns = """
        id,
        lost_reporter_id,
        finder_id,
        claimant_id,
        status,
        is_active,
        confidence_score,
        created_at,
        lost_snapshot,
        found_snapshot
        """;

    private const string VisibleFilter = """
        is_active = 1
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
        """;

    private readonly IDbConnectionFactory _connections;

    public MatchReadRepository(IDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task<StoredMatchPage> GetPageAsync(
        Guid userId,
        string section,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        // Only fixed SQL fragments are selected here. Request text is
        // never inserted into SQL.
        var sectionFilter = GetSectionFilter(section);
        var filter = $"{VisibleFilter} AND ({sectionFilter})";

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        // Keep the count and page consistent if another request changes
        // a match while this page is being read.
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

        long totalCount;

        await using (var count = new MySqlCommand(
            $"SELECT COUNT(*) FROM matches WHERE {filter};",
            connection,
            transaction))
        {
            count.Parameters.AddWithValue("@userId", userId);

            totalCount = Convert.ToInt64(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        var sql = $"""
            SELECT {Columns}
            FROM matches
            WHERE {filter}
            ORDER BY created_at DESC, id DESC
            LIMIT @size OFFSET @offset;
            """;

        var records = new List<StoredMatch>();

        await using (var command = new MySqlCommand(
            sql,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@size", size);
            command.Parameters.AddWithValue(
                "@offset",
                ((long)page - 1) * size);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(ReadMatch(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new StoredMatchPage(records, totalCount);
    }

    public async Task<StoredMatch?> GetByIdAsync(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {Columns}
            FROM matches
            WHERE id = @matchId
            LIMIT 1;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@matchId", matchId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? ReadMatch(reader)
            : null;
    }

    private static string GetSectionFilter(string section)
    {
        return section switch
        {
            "active" => """
                status IN (
                    'LOST_REPORTER_CONFIRMED',
                    'FINDER_CONFIRMED'
                )
                """,

            "waiting-on-you" => """
                (
                    status = 'FINDER_CONFIRMED'
                    AND lost_reporter_id = @userId
                )
                OR (
                    status = 'LOST_REPORTER_CONFIRMED'
                    AND finder_id = @userId
                )
                """,

            "waiting-on-other" => """
                (
                    status = 'LOST_REPORTER_CONFIRMED'
                    AND lost_reporter_id = @userId
                )
                OR (
                    status = 'FINDER_CONFIRMED'
                    AND finder_id = @userId
                )
                """,

            "confirmed" => "status = 'CONFIRMED'",
            "rejected" => "status = 'REJECTED'",
            "closed" => "status IN ('CONFIRMED', 'REJECTED')",
            "all" => "1 = 1",

            _ => throw new ArgumentOutOfRangeException(
                nameof(section))
        };
    }

    private static StoredMatch ReadMatch(
        MySqlDataReader reader)
    {
        var lost = ReadSnapshot(reader.GetString(8));
        var found = ReadSnapshot(reader.GetString(9));

        return new StoredMatch(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetDecimal(6),
            DateTime.SpecifyKind(
                reader.GetDateTime(7),
                DateTimeKind.Utc),
            lost,
            found);
    }

    private static ClaimItemView ReadSnapshot(string json)
    {
        return JsonSerializer.Deserialize<ClaimItemView>(
                   json,
                   JsonOptions)
               ?? throw new InvalidDataException(
                   "A match contains an invalid item snapshot.");
    }
}