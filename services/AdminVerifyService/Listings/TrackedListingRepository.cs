using MySqlConnector;

namespace AdminVerifyService.Listings;

public sealed class TrackedListingRepository
{
    public async Task<bool> MarkProcessedAsync(
        ListingEvent listingEvent,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT IGNORE INTO processed_events (event_id, topic, processed_at)
            VALUES (@eventId, @topic, @now);
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", listingEvent.EventId);
        command.Parameters.AddWithValue("@topic", listingEvent.Topic);
        command.Parameters.AddWithValue("@now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TrackAsync(
        ListingEvent listingEvent,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT IGNORE INTO tracked_listings (listing_id, listing_type, user_id, posted_at, created_at)
            VALUES (@listingId, @listingType, @userId, @postedAt, @now);
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@listingId", listingEvent.ListingId);
        command.Parameters.AddWithValue("@listingType", listingEvent.ListingType);
        command.Parameters.AddWithValue("@userId", listingEvent.UserId);
        command.Parameters.AddWithValue("@postedAt", listingEvent.PostedAt);
        command.Parameters.AddWithValue("@now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
