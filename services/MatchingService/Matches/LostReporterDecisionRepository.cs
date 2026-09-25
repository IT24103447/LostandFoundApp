using System.Data;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Matches;

public sealed record LostReporterDecisionResult(
    Guid MatchId,
    string Status,
    DateTime DecidedAt);

public sealed record FinderReturnContact(
    string Email,
    string Phone);

public sealed class LostReporterDecisionRepository
{
    private readonly IDbConnectionFactory _connections;
    private readonly TimeProvider _time;

    public LostReporterDecisionRepository(
        IDbConnectionFactory connections,
        TimeProvider time)
    {
        _connections = connections;
        _time = time;
    }

    public async Task<LostReporterDecisionResult> DecideAsync(
        Guid matchId,
        Guid userId,
        bool confirm,
        CancellationToken cancellationToken,
        string? lostReporterEmail = null,
        string? lostReporterPhone = null)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Guid lostReporterId;
        string previousStatus;
        bool isActive;

        // Lock the match so simultaneous decisions cannot both succeed.
        const string selectSql = """
            SELECT lost_reporter_id, status, is_active
            FROM matches
            WHERE id = @matchId
            FOR UPDATE;
            """;

        await using (var select = new MySqlCommand(
            selectSql,
            connection,
            transaction))
        {
            select.Parameters.AddWithValue("@matchId", matchId);

            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ClaimException(
                    404,
                    "Match not found.");
            }

            lostReporterId = reader.GetGuid(0);
            previousStatus = reader.GetString(1);
            isActive = reader.GetBoolean(2);
        }

        if (lostReporterId != userId)
        {
            throw new ClaimException(
                403,
                "Only this match's lost reporter can make this decision.");
        }

        if (!isActive ||
            previousStatus != "FINDER_CONFIRMED")
        {
            throw new ClaimException(
                409,
                "This match is no longer awaiting your decision. Refresh the page.");
        }

        if (confirm &&
            (string.IsNullOrWhiteSpace(lostReporterEmail) ||
             string.IsNullOrWhiteSpace(lostReporterPhone)))
        {
            throw new ClaimException(
                409,
                "Your contact details are unavailable. Please sign in again before confirming.");
        }

        var newStatus = confirm
            ? "CONFIRMED"
            : "REJECTED";

        var action = confirm
            ? "CONFIRM"
            : "REJECT";

        var now = _time.GetUtcNow().UtcDateTime;

        const string updateSql = """
            UPDATE matches
            SET status = @status,
                updated_at = @now,
                lost_reporter_email =
                    CASE WHEN @confirm = 1
                         THEN @email
                         ELSE lost_reporter_email
                    END,
                lost_reporter_phone =
                    CASE WHEN @confirm = 1
                         THEN @phone
                         ELSE lost_reporter_phone
                    END
            WHERE id = @matchId;
            """;

        await using (var update = new MySqlCommand(
            updateSql,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(
                "@status",
                newStatus);

            update.Parameters.AddWithValue(
                "@now",
                now);

            update.Parameters.AddWithValue(
                "@matchId",
                matchId);

            update.Parameters.AddWithValue(
                "@confirm",
                confirm);

            update.Parameters.AddWithValue(
                "@email",
                confirm
                    ? (object)lostReporterEmail!.Trim()
                    : DBNull.Value);

            update.Parameters.AddWithValue(
                "@phone",
                confirm
                    ? (object)lostReporterPhone!.Trim()
                    : DBNull.Value);

            await update.ExecuteNonQueryAsync(
                cancellationToken);
        }

        const string auditSql = """
            INSERT INTO match_actions (
                id,
                match_id,
                actor_user_id,
                actor_role,
                action,
                previous_status,
                new_status,
                created_at
            )
            VALUES (
                @id,
                @matchId,
                @userId,
                'LOST',
                @action,
                @previousStatus,
                @newStatus,
                @now
            );
            """;

        await using (var audit = new MySqlCommand(
            auditSql,
            connection,
            transaction))
        {
            audit.Parameters.AddWithValue(
                "@id",
                Guid.NewGuid());

            audit.Parameters.AddWithValue(
                "@matchId",
                matchId);

            audit.Parameters.AddWithValue(
                "@userId",
                userId);

            audit.Parameters.AddWithValue(
                "@action",
                action);

            audit.Parameters.AddWithValue(
                "@previousStatus",
                previousStatus);

            audit.Parameters.AddWithValue(
                "@newStatus",
                newStatus);

            audit.Parameters.AddWithValue(
                "@now",
                now);

            await audit.ExecuteNonQueryAsync(
                cancellationToken);
        }

        if (confirm)
        {
            await MatchConfirmationOutbox.EnqueueAsync(
                connection, transaction, matchId, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new LostReporterDecisionResult(
            matchId,
            newStatus,
            now);
    }

    public async Task<FinderReturnContact> GetFinderContactAsync(
        Guid matchId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                lost_reporter_id,
                status,
                is_active,
                finder_email,
                finder_phone
            FROM matches
            WHERE id = @matchId;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@matchId",
            matchId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ClaimException(
                404,
                "Match not found.");
        }

        if (reader.GetGuid(0) != userId)
        {
            throw new ClaimException(
                403,
                "You do not have permission to view these contact details.");
        }

        if (reader.GetString(1) != "CONFIRMED" ||
            !reader.GetBoolean(2))
        {
            throw new ClaimException(
                409,
                "Contact details are available only for a confirmed active match.");
        }

        if (reader.IsDBNull(3) ||
            reader.IsDBNull(4))
        {
            throw ContactUnavailable();
        }

        var email = reader.GetString(3);
        var phone = reader.GetString(4);

        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(phone))
        {
            throw ContactUnavailable();
        }

        return new FinderReturnContact(
            email,
            phone);
    }

    private static ClaimException ContactUnavailable()
    {
        return new ClaimException(
            503,
            "The finder's contact details were not saved for this claim.");
    }
}
