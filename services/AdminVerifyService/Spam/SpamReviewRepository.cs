using System.Text;
using AdminVerifyService.Databases;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public sealed class SpamReviewRepository(IDbConnectionFactory connections)
{
    public const int PageSize = 20;

    public async Task<SpamRecordPage> ListAsync(
        SpamRecordListQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand { Connection = connection };

        var sql = new StringBuilder("""
            SELECT id, user_id, score_a, status, created_at
            FROM spam_records
            WHERE status IN (
            """);

        AppendList(sql, command, "@status", query.Statuses);
        sql.Append(')');

        if (query.UserIds.Count > 0)
        {
            sql.Append(" AND user_id IN (");
            AppendList(sql, command, "@user", query.UserIds);
            sql.Append(')');
        }

        if (query.FlaggedFrom is not null)
        {
            sql.Append(" AND created_at >= @flaggedFrom");
            command.Parameters.AddWithValue("@flaggedFrom", query.FlaggedFrom.Value);
        }

        if (query.FlaggedBefore is not null)
        {
            sql.Append(" AND created_at < @flaggedBefore");
            command.Parameters.AddWithValue("@flaggedBefore", query.FlaggedBefore.Value);
        }

        sql.Append(query.SortByScore
            ? " ORDER BY score_a DESC, created_at DESC, id"
            : " ORDER BY created_at DESC, id");

        sql.Append(" LIMIT @limit OFFSET @offset;");
        command.Parameters.AddWithValue("@limit", PageSize + 1);
        command.Parameters.AddWithValue("@offset", (query.Page - 1) * PageSize);

        command.CommandText = sql.ToString();

        var rows = new List<SpamRecordRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var scoreA = reader.GetInt32(2);

            rows.Add(new SpamRecordRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                scoreA,
                scoreA,
                reader.GetString(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));
        }

        var hasMore = rows.Count > PageSize;

        return new SpamRecordPage(hasMore ? rows[..PageSize] : rows, hasMore);
    }

    private static void AppendList<T>(
        StringBuilder sql,
        MySqlCommand command,
        string prefix,
        IReadOnlyList<T> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"{prefix}{i}";

            sql.Append(i == 0 ? name : $", {name}");
            command.Parameters.AddWithValue(name, values[i]);
        }
    }
}
