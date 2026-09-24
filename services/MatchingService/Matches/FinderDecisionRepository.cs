using System.Data;
using MatchingService.Claims;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Matches;

public sealed record FinderDecisionResult(
    Guid MatchId,
    string Status,
    DateTime DecidedAt);

public sealed record LostReporterReturnContact(
    string Email,
    string Phone);

public sealed class FinderDecisionRepository
{
    private readonly IDbConnectionFactory _connections;
    private readonly TimeProvider _time;

    public FinderDecisionRepository(
        IDbConnectionFactory connections,
        TimeProvider time)
    {
        _connections = connections;
        _time = time;
    }

    public async Task<FinderDecisionResult> DecideAsync(
        Guid matchId,
        Guid userId,
        bool confirm,
        string? finderEmail,
        string? finderPhone,
        CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Guid finderId;
        string previousStatus;
        bool isActive;

        const string selectSql = """
            SELECT finder_id, status, is_active
            FROM matches
            WHERE id = @matchId
            FOR UPDATE;
            """;

        await using (var select = new MySqlCommand(
            selectSql, connection, transaction))
        {
            select.Parameters.AddWithValue("@matchId", matchId);

            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ClaimException(404, "Match not found.");
            }

            finderId = reader.GetGuid(0);
            previousStatus = reader.GetString(1);
            isActive = reader.GetBoolean(2);
        }

        if (finderId != userId)
        {
            throw new ClaimException(
                403,
                "Only this match's finder can make this decision.");
        }

        if (!isActive ||
            previousStatus != "LOST_REPORTER_CONFIRMED")
        {
            throw new ClaimException(
                409,
                "This match is no longer awaiting your decision. Refresh the page.");
        }

        if (confirm &&
            (string.IsNullOrWhiteSpace(finderEmail) ||
             string.IsNullOrWhiteSpace(finderPhone)))
        {
            throw new ClaimException(
                409,
                "Your contact details are unavailable. Please sign in again before confirming.");
        }

        var newStatus = confirm ? "CONFIRMED" : "REJECTED";
        var action = confirm ? "CONFIRM" : "REJECT";
        var now = _time.GetUtcNow().UtcDateTime;

        const string updateSql = """
            UPDATE matches
            SET status = @status,
                updated_at = @now,
                finder_email =
                    CASE WHEN @confirm = 1
                         THEN @email
                         ELSE finder_email
                    END,
                finder_phone =
                    CASE WHEN @confirm = 1
                         THEN @phone
                         ELSE finder_phone
                    END
            WHERE id = @matchId;
            """;

        await using (var update = new MySqlCommand(
            updateSql, connection, transaction))
        {
            update.Parameters.AddWithValue("@status", newStatus);
            update.Parameters.AddWithValue("@now", now);
            update.Parameters.AddWithValue("@matchId", matchId);
            update.Parameters.AddWithValue("@confirm", confirm);

            update.Parameters.AddWithValue(
                "@email",
                confirm ? finderEmail!.Trim() : DBNull.Value);

            update.Parameters.AddWithValue(
                "@phone",
                confirm ? finderPhone!.Trim() : DBNull.Value);

            await update.ExecuteNonQueryAsync(cancellationToken);
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
                'FOUND',
                @action,
                @previousStatus,
                @newStatus,
                @now
            );
            """;

        await using (var audit = new MySqlCommand(
            auditSql, connection, transaction))
        {
            audit.Parameters.AddWithValue("@id", Guid.NewGuid());
            audit.Parameters.AddWithValue("@matchId", matchId);
            audit.Parameters.AddWithValue("@userId", userId);
            audit.Parameters.AddWithValue("@action", action);
            audit.Parameters.AddWithValue(
                "@previousStatus",
                previousStatus);
            audit.Parameters.AddWithValue("@newStatus", newStatus);
            audit.Parameters.AddWithValue("@now", now);

            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new FinderDecisionResult(
            matchId,
            newStatus,
            now);
    }

    public async Task<LostReporterReturnContact>
        GetLostReporterContactAsync(
            Guid matchId,
            Guid userId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                finder_id,
                status,
                is_active,
                lost_reporter_email,
                lost_reporter_phone
            FROM matches
            WHERE id = @matchId;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue("@matchId", matchId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ClaimException(404, "Match not found.");
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

        return new LostReporterReturnContact(email, phone);
    }

    private static ClaimException ContactUnavailable()
    {
        return new ClaimException(
            503,
            "The lost reporter's contact details were not saved for this claim.");
    }
}