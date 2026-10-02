using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Appeals;

public sealed class AppealRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    public const int PageSize = 20;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const string Columns = """
        id, lost_item_id, found_item_id,
        lost_reporter_id, finder_id,
        appellant_id, appellant_role,
        appellant_email, appellant_phone,
        score, score_breakdown,
        lost_snapshot, found_snapshot,
        note, status, decided_by, decided_at, created_at,
        rejection_reason
        """;

    private const string WaitingMatchCondition = """
        a.status = 'PENDING'
        OR (a.status = 'VERIFIED'
            AND m.is_active = 1
            AND m.status IN (
                'LOST_REPORTER_CONFIRMED',
                'FINDER_CONFIRMED'
            ))
        """;

    private const string LostEditWarningSql = $"""
        SELECT 1
        FROM match_appeals AS a
        LEFT JOIN matches AS m
            ON m.lost_item_id = a.lost_item_id
           AND m.found_item_id = a.found_item_id
        WHERE a.lost_item_id = @itemId
          AND a.lost_reporter_id = @userId
          AND ({WaitingMatchCondition})
        LIMIT 1;
        """;

    private const string FoundEditWarningSql = $"""
        SELECT 1
        FROM match_appeals AS a
        LEFT JOIN matches AS m
            ON m.lost_item_id = a.lost_item_id
           AND m.found_item_id = a.found_item_id
        WHERE a.found_item_id = @itemId
          AND a.finder_id = @userId
          AND ({WaitingMatchCondition})
        LIMIT 1;
        """;

    public async Task<AppealRecord> CreateAsync(
        NewAppeal appeal,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = time.GetUtcNow().UtcDateTime;

        const string sql = """
            INSERT INTO match_appeals (
                id, lost_item_id, found_item_id,
                lost_reporter_id, finder_id,
                appellant_id, appellant_role,
                appellant_email, appellant_phone,
                score, score_breakdown,
                lost_snapshot, found_snapshot,
                note, status, created_at, updated_at
            )
            VALUES (
                @id, @lostItemId, @foundItemId,
                @lostReporterId, @finderId,
                @appellantId, @appellantRole,
                @appellantEmail, @appellantPhone,
                @score, @breakdown,
                @lostSnapshot, @foundSnapshot,
                @note, 'PENDING', @now, @now
            );
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", appeal.LostItemId);
        command.Parameters.AddWithValue("@foundItemId", appeal.FoundItemId);
        command.Parameters.AddWithValue("@lostReporterId", appeal.LostReporterId);
        command.Parameters.AddWithValue("@finderId", appeal.FinderId);
        command.Parameters.AddWithValue("@appellantId", appeal.AppellantId);
        command.Parameters.AddWithValue("@appellantRole", appeal.AppellantRole);
        command.Parameters.AddWithValue("@appellantEmail", appeal.AppellantEmail.Trim());
        command.Parameters.AddWithValue("@appellantPhone", appeal.AppellantPhone.Trim());
        command.Parameters.AddWithValue("@score", appeal.Score);
        command.Parameters.AddWithValue(
            "@breakdown", JsonSerializer.Serialize(appeal.Breakdown, JsonOptions));
        command.Parameters.AddWithValue(
            "@lostSnapshot", JsonSerializer.Serialize(appeal.Lost, JsonOptions));
        command.Parameters.AddWithValue(
            "@foundSnapshot", JsonSerializer.Serialize(appeal.Found, JsonOptions));
        command.Parameters.AddWithValue(
            "@note", string.IsNullOrWhiteSpace(appeal.Note) ? DBNull.Value : appeal.Note.Trim());
        command.Parameters.AddWithValue("@now", now);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new ClaimException(
                409, "This pair has already been submitted for appeal.");
        }

        return new AppealRecord(
            id,
            appeal.LostItemId,
            appeal.FoundItemId,
            appeal.LostReporterId,
            appeal.FinderId,
            appeal.AppellantId,
            appeal.AppellantRole,
            appeal.AppellantEmail.Trim(),
            appeal.AppellantPhone.Trim(),
            appeal.Score,
            appeal.Breakdown,
            appeal.Lost,
            appeal.Found,
            string.IsNullOrWhiteSpace(appeal.Note) ? null : appeal.Note.Trim(),
            AppealStatus.Pending,
            null,
            null,
            now,
            null);
    }

    public async Task<bool> PairHasAppealAsync(
        Guid lostItemId,
        Guid foundItemId,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM match_appeals
            WHERE lost_item_id = @lostItemId
              AND found_item_id = @foundItemId
              AND (lost_reporter_id = @ownerId OR finder_id = @ownerId)
            LIMIT 1;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@ownerId", ownerId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<List<AppealRecord>> GetByAppellantAsync(
        Guid appellantId,
        int page,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {Columns}
            FROM match_appeals
            WHERE appellant_id = @appellantId
            ORDER BY created_at DESC, id DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@appellantId", appellantId);
        AddPaging(command, page);

        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<List<AppealRecord>> ListByStatusAsync(
        string status,
        int page,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {Columns}
            FROM match_appeals
            WHERE status = @status
            ORDER BY created_at DESC, id DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@status", status);
        AddPaging(command, page);

        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<AppealRecord?> GetAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = $"""
            SELECT {Columns}
            FROM match_appeals
            WHERE id = @id
            LIMIT 1;
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);

        var appeals = await ReadAllAsync(command, cancellationToken);
        return appeals.Count == 0 ? null : appeals[0];
    }

    public async Task<bool> HasEditWarningAsync(
        string itemType,
        Guid itemId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var sql = itemType == "LOST"
            ? LostEditWarningSql
            : FoundEditWarningSql;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@itemId", itemId);
        command.Parameters.AddWithValue("@userId", userId);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> DecideAsync(
        Guid id,
        string status,
        Guid adminId,
        string? rejectionReason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE match_appeals
            SET status = @status,
                decided_by = @adminId,
                decided_at = @now,
                rejection_reason = @reason,
                updated_at = @now
            WHERE id = @id
              AND status = 'PENDING';
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@adminId", adminId);
        command.Parameters.AddWithValue(
            "@reason",
            string.IsNullOrWhiteSpace(rejectionReason) ? DBNull.Value : rejectionReason.Trim());
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReopenAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE match_appeals
            SET status = 'PENDING',
                decided_by = NULL,
                decided_at = NULL,
                updated_at = @now
            WHERE id = @id
              AND status = 'VERIFIED';
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPaging(
        MySqlCommand command,
        int page)
    {
        command.Parameters.AddWithValue("@limit", PageSize);
        command.Parameters.AddWithValue(
            "@offset", ((long)Math.Max(1, page) - 1) * PageSize);
    }

    private static async Task<List<AppealRecord>> ReadAllAsync(
        MySqlCommand command,
        CancellationToken cancellationToken)
    {
        var appeals = new List<AppealRecord>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            appeals.Add(new AppealRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetGuid(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDecimal(9),
                ReadJson<ScoreBreakdown>(reader.GetString(10)),
                ReadJson<ClaimItemView>(reader.GetString(11)),
                ReadJson<ClaimItemView>(reader.GetString(12)),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetGuid(15),
                reader.IsDBNull(16)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(16), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(17), DateTimeKind.Utc),
                reader.IsDBNull(18) ? null : reader.GetString(18)));
        }

        return appeals;
    }

    private static T ReadJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("An appeal contains invalid stored data.");
}
