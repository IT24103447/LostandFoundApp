using AdminVerifyService.Listings;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public static class SpamRecordStatus
{
    public const string NeedsReview = "NEEDS_REVIEW";
    public const string UnderReview = "UNDER_REVIEW";
    public const string PendingSolve = "PENDING_SOLVE";
    public const string Solved = "SOLVED";
    public const string Dismissed = "DISMISSED";
}

public sealed record CollectingRecord(
    Guid Id,
    string Status,
    DateTime CollectingUntil,
    int ScoreA);

public sealed class SpamRecordRepository
{
    public async Task<CollectingRecord?> LockCollectingAsync(
        Guid userId,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, status, collecting_until, score_a
            FROM spam_records
            WHERE collecting_user_id = @userId
            FOR UPDATE;
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CollectingRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            reader.GetInt32(3));
    }

    public async Task AddListingAsync(
        Guid recordId,
        Guid listingId,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO spam_record_listings (spam_record_id, listing_id, added_at)
            VALUES (@recordId, @listingId, @now);

            UPDATE spam_records
            SET score_a = score_a + 1,
                updated_at = @now
            WHERE id = @recordId;
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@listingId", listingId);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StopCollectingAsync(
        Guid recordId,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE spam_records
            SET collecting_user_id = NULL,
                updated_at = @now
            WHERE id = @recordId;
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@recordId", recordId);
        command.Parameters.AddWithValue("@now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindFreeListingsAsync(
        ListingEvent listing,
        TimeSpan window,
        int cap,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.listing_id
            FROM tracked_listings AS t
            WHERE t.user_id = @userId
              AND t.posted_at >= @from
              AND t.posted_at <= @postedAt
              AND NOT EXISTS (
                  SELECT 1 FROM spam_record_listings AS s
                  WHERE s.listing_id = t.listing_id)
            ORDER BY t.posted_at DESC, t.listing_id
            LIMIT @limit
            FOR UPDATE;
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@userId", listing.UserId);
        command.Parameters.AddWithValue("@from", listing.PostedAt - window);
        command.Parameters.AddWithValue("@postedAt", listing.PostedAt);
        command.Parameters.AddWithValue("@limit", cap > 0 ? cap : int.MaxValue);

        var listingIds = new List<Guid>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            listingIds.Add(reader.GetGuid(0));
        }

        return listingIds;
    }

    public async Task<int> CreateAsync(
        Guid recordId,
        ListingEvent listing,
        IReadOnlyList<Guid> listingIds,
        DateTime collectingUntil,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string recordSql = """
            INSERT INTO spam_records
                (id, user_id, collecting_user_id, score_a, status, collecting_until, created_at, updated_at)
            VALUES
                (@recordId, @userId, @userId, @scoreA, 'NEEDS_REVIEW', @collectingUntil, @now, @now);
            """;

        await using (var record = new MySqlCommand(recordSql, connection, transaction))
        {
            record.Parameters.AddWithValue("@recordId", recordId);
            record.Parameters.AddWithValue("@userId", listing.UserId);
            record.Parameters.AddWithValue("@scoreA", listingIds.Count);
            record.Parameters.AddWithValue("@collectingUntil", collectingUntil);
            record.Parameters.AddWithValue("@now", now);

            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        const string listingSql = """
            INSERT INTO spam_record_listings (spam_record_id, listing_id, added_at)
            VALUES (@recordId, @listingId, @now);
            """;

        foreach (var listingId in listingIds)
        {
            await using var link = new MySqlCommand(listingSql, connection, transaction);
            link.Parameters.AddWithValue("@recordId", recordId);
            link.Parameters.AddWithValue("@listingId", listingId);
            link.Parameters.AddWithValue("@now", now);

            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        const string notificationSql = """
            INSERT INTO spam_record_notifications
                (id, spam_record_id, recipient_email, status, attempts, next_attempt_at, created_at, updated_at)
            SELECT UUID(), @recordId, email, 'PENDING', 0, @now, @now, @now
            FROM spam_alert_recipients;
            """;

        await using var notifications = new MySqlCommand(notificationSql, connection, transaction);
        notifications.Parameters.AddWithValue("@recordId", recordId);
        notifications.Parameters.AddWithValue("@now", now);

        return await notifications.ExecuteNonQueryAsync(cancellationToken);
    }
}
