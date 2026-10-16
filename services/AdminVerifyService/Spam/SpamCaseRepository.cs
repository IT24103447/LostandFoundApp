using AdminVerifyService.Databases;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public sealed class SpamCaseRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    internal const string RecordNotFound = "Spam record not found.";
    internal const string AlreadyHandled = "This record is already being handled.";

    public async Task<SpamRecordDetail> GetDetailAsync(Guid recordId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        return await ReadDetailAsync(recordId, connection, null, cancellationToken)
            ?? throw new SpamReviewException(StatusCodes.Status404NotFound, RecordNotFound);
    }

    public async Task<SpamRecordDetail> OpenAsync(Guid recordId, CancellationToken cancellationToken)
    {
        const string openSql = """
            UPDATE spam_records
            SET status = 'UNDER_REVIEW',
                collecting_user_id = NULL,
                updated_at = @now
            WHERE id = @recordId
              AND status = 'NEEDS_REVIEW';
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using (var open = new MySqlCommand(openSql, connection))
        {
            open.Parameters.AddWithValue("@recordId", recordId);
            open.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);
            await open.ExecuteNonQueryAsync(cancellationToken);
        }

        return await ReadDetailAsync(recordId, connection, null, cancellationToken)
            ?? throw new SpamReviewException(StatusCodes.Status404NotFound, RecordNotFound);
    }

    public async Task<SpamRecordDetail> DismissAsync(Guid recordId, CancellationToken cancellationToken)
    {
        const string dismissSql = """
            UPDATE spam_records
            SET status = 'DISMISSED',
                collecting_user_id = NULL,
                updated_at = @now
            WHERE id = @recordId
              AND status = 'UNDER_REVIEW';
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        int changed;

        await using (var dismiss = new MySqlCommand(dismissSql, connection))
        {
            dismiss.Parameters.AddWithValue("@recordId", recordId);
            dismiss.Parameters.AddWithValue("@now", time.GetUtcNow().UtcDateTime);
            changed = await dismiss.ExecuteNonQueryAsync(cancellationToken);
        }

        var detail = await ReadDetailAsync(recordId, connection, null, cancellationToken)
            ?? throw new SpamReviewException(StatusCodes.Status404NotFound, RecordNotFound);

        return changed > 0
            ? detail
            : throw new SpamReviewException(StatusCodes.Status409Conflict, AlreadyHandled);
    }

    internal static async Task<SpamRecordDetail?> ReadDetailAsync(
        Guid recordId,
        MySqlConnection connection,
        MySqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string recordSql = """
            SELECT id, user_id, score_a, status, created_at, kick_status
            FROM spam_records
            WHERE id = @recordId;
            """;

        const string listingsSql = """
            SELECT s.listing_id, t.listing_type, t.posted_at, s.solve_result
            FROM spam_record_listings AS s
            JOIN tracked_listings AS t ON t.listing_id = s.listing_id
            WHERE s.spam_record_id = @recordId
            ORDER BY t.posted_at, s.listing_id;
            """;

        Guid userId;
        int scoreA;
        string status;
        DateTime flaggedAt;
        string kickStatus;

        await using (var record = new MySqlCommand(recordSql, connection, transaction))
        {
            record.Parameters.AddWithValue("@recordId", recordId);

            await using var reader = await record.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            userId = reader.GetGuid(1);
            scoreA = reader.GetInt32(2);
            status = reader.GetString(3);
            flaggedAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
            kickStatus = reader.GetString(5);
        }

        var listings = new List<SpamRecordListing>();

        await using (var command = new MySqlCommand(listingsSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@recordId", recordId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                listings.Add(new SpamRecordListing(
                    reader.GetGuid(0),
                    reader.GetString(1).ToLowerInvariant(),
                    DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new SpamRecordDetail(recordId, userId, scoreA, scoreA, status, flaggedAt, kickStatus, listings);
    }
}
