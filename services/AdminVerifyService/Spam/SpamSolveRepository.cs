using AdminVerifyService.Databases;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public sealed class SpamSolveRepository(
    IDbConnectionFactory connections,
    TimeProvider time)
{
    private const string RecordIdParam = "@recordId";
    private const string NowParam = "@now";
    private const string NotPendingSolve = "This record is not being solved.";

    private static readonly string[] ListingResults = [SolveResult.Deleted, SolveResult.Skipped, SolveResult.Failed];
    private static readonly string[] KickResults = [KickStatus.Kicked, KickStatus.Failed];

    public async Task<SpamRecordDetail> StartAsync(Guid recordId, SolveRequest request, CancellationToken cancellationToken)
    {
        const string startSql = """
            UPDATE spam_records
            SET status = 'PENDING_SOLVE',
                kick_status = @kickStatus,
                collecting_user_id = NULL,
                updated_at = @now
            WHERE id = @recordId
              AND status = 'UNDER_REVIEW';
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        if (!request.Resume)
        {
            await using var start = new MySqlCommand(startSql, connection);
            start.Parameters.AddWithValue(RecordIdParam, recordId);
            start.Parameters.AddWithValue("@kickStatus", request.Kick ? KickStatus.Pending : KickStatus.NotRequested);
            start.Parameters.AddWithValue(NowParam, time.GetUtcNow().UtcDateTime);

            if (await start.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await ReadOrNotFoundAsync(recordId, connection, cancellationToken);
                throw new SpamReviewException(StatusCodes.Status409Conflict, SpamCaseRepository.AlreadyHandled);
            }
        }

        var detail = await ReadOrNotFoundAsync(recordId, connection, cancellationToken);

        return detail.Status == SpamRecordStatus.PendingSolve
            ? detail
            : throw new SpamReviewException(StatusCodes.Status409Conflict, SpamCaseRepository.AlreadyHandled);
    }

    public async Task SaveListingResultAsync(
        Guid recordId,
        Guid listingId,
        string? result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE spam_record_listings AS s
            JOIN spam_records AS r ON r.id = s.spam_record_id
            SET s.solve_result = @result
            WHERE s.spam_record_id = @recordId
              AND s.listing_id = @listingId
              AND r.status = 'PENDING_SOLVE'
              AND (s.solve_result IS NULL OR s.solve_result <> 'DELETED');
            """;

        var value = Validate(result, ListingResults, "Result must be DELETED, SKIPPED or FAILED.");

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue(RecordIdParam, recordId);
        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@result", value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
        {
            return;
        }

        var detail = await ReadOrNotFoundAsync(recordId, connection, cancellationToken);

        if (detail.Status != SpamRecordStatus.PendingSolve)
        {
            throw new SpamReviewException(StatusCodes.Status409Conflict, NotPendingSolve);
        }

        if (detail.Listings.All(listing => listing.ListingId != listingId))
        {
            throw new SpamReviewException(StatusCodes.Status404NotFound, "Listing is not in this record.");
        }
    }

    public async Task<SpamRecordDetail> SaveKickResultAsync(
        Guid recordId,
        string? result,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE spam_records
            SET kick_status = @result,
                updated_at = @now
            WHERE id = @recordId
              AND kick_status = 'PENDING';
            """;

        var value = Validate(result, KickResults, "Result must be KICKED or FAILED.");

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        int changed;

        await using (var command = new MySqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue(RecordIdParam, recordId);
            command.Parameters.AddWithValue("@result", value);
            command.Parameters.AddWithValue(NowParam, time.GetUtcNow().UtcDateTime);
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var detail = await ReadOrNotFoundAsync(recordId, connection, cancellationToken);

        return changed > 0
            ? detail
            : throw new SpamReviewException(
                StatusCodes.Status409Conflict,
                "The kick was not requested or has already been attempted.");
    }

    public async Task<SpamRecordDetail> FinishAsync(Guid recordId, CancellationToken cancellationToken)
    {
        const string finishSql = """
            UPDATE spam_records AS r
            SET r.status = 'SOLVED',
                r.updated_at = @now
            WHERE r.id = @recordId
              AND r.status = 'PENDING_SOLVE'
              AND r.kick_status <> 'PENDING'
              AND NOT EXISTS (
                  SELECT 1 FROM spam_record_listings AS s
                  WHERE s.spam_record_id = r.id
                    AND (s.solve_result IS NULL OR s.solve_result = 'FAILED'));
            """;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        int changed;

        await using (var command = new MySqlCommand(finishSql, connection))
        {
            command.Parameters.AddWithValue(RecordIdParam, recordId);
            command.Parameters.AddWithValue(NowParam, time.GetUtcNow().UtcDateTime);
            changed = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var detail = await ReadOrNotFoundAsync(recordId, connection, cancellationToken);

        return changed > 0
            ? detail
            : throw new SpamReviewException(StatusCodes.Status409Conflict, FinishBlockedReason(detail));
    }

    private static string FinishBlockedReason(SpamRecordDetail detail)
    {
        if (detail.Status != SpamRecordStatus.PendingSolve)
        {
            return SpamCaseRepository.AlreadyHandled;
        }

        return detail.KickStatus == KickStatus.Pending
            ? "The kick has not been attempted yet."
            : "Some listings have not been deleted or skipped yet.";
    }

    private static string Validate(string? value, string[] allowed, string message)
    {
        var normalised = value?.Trim().ToUpperInvariant();

        return normalised is not null && allowed.Contains(normalised)
            ? normalised
            : throw new SpamReviewException(StatusCodes.Status400BadRequest, message);
    }

    private static async Task<SpamRecordDetail> ReadOrNotFoundAsync(
        Guid recordId,
        MySqlConnection connection,
        CancellationToken cancellationToken) =>
        await SpamCaseRepository.ReadDetailAsync(recordId, connection, null, cancellationToken)
            ?? throw new SpamReviewException(StatusCodes.Status404NotFound, SpamCaseRepository.RecordNotFound);
}
