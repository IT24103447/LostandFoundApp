using System.Text.Json;
using MatchingService.Claims;
using MySqlConnector;

namespace MatchingService.Matches;

public static class MatchConfirmationOutbox
{
    public const string Topic = "matches.confirmed";

    public static async Task EnqueueAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Guid matchId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        Guid lostItemId;
        Guid foundItemId;
        Guid lostReporterId;
        Guid finderId;

        await using (var select = new MySqlCommand("""
            SELECT lost_item_id, found_item_id, lost_reporter_id, finder_id
            FROM matches
            WHERE id = @id;
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("@id", matchId);

            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ClaimException(404, "Match not found.");
            }

            lostItemId = reader.GetGuid(0);
            foundItemId = reader.GetGuid(1);
            lostReporterId = reader.GetGuid(2);
            finderId = reader.GetGuid(3);
        }

        // Older confirmed matches may predate the confirmation outbox.
        await using (var existing = new MySqlCommand("""
            SELECT 1
            FROM matches
            WHERE id <> @matchId
              AND status = 'CONFIRMED'
              AND is_active = 1
              AND (
                  lost_item_id = @lostId
                  OR found_item_id = @foundId
              )
            LIMIT 1;
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("@matchId", matchId);
            existing.Parameters.AddWithValue("@lostId", lostItemId);
            existing.Parameters.AddWithValue("@foundId", foundItemId);

            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new ClaimException(
                    409,
                    "One of these reports has already been confirmed in another match. Refresh the page.");
            }
        }

        var eventId = Guid.NewGuid();

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventType = "match.confirmed",
            eventId,
            matchId,
            lostItemId,
            foundItemId,
            lostReporterId,
            finderId,
            confirmedAt = DateTime.SpecifyKind(now, DateTimeKind.Utc)
        });

        try
        {
            // Unique item keys prevent two matches from confirming the same report.
            await using (var insert = new MySqlCommand("""
                INSERT INTO match_confirmation_outbox (
                    event_id, match_id, lost_item_id, found_item_id,
                    topic, payload, created_at, next_attempt_at
                )
                VALUES (
                    @eventId, @matchId, @lostId, @foundId,
                    @topic, @payload, @now, @now
                );
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@eventId", eventId);
                insert.Parameters.AddWithValue("@matchId", matchId);
                insert.Parameters.AddWithValue("@lostId", lostItemId);
                insert.Parameters.AddWithValue("@foundId", foundItemId);
                insert.Parameters.AddWithValue("@topic", Topic);
                insert.Parameters.AddWithValue("@payload", payload);
                insert.Parameters.AddWithValue("@now", now);

                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await BackfillPhotoAsync(
                connection, transaction, matchId,
                lostItemId, "LOST", cancellationToken);

            await BackfillPhotoAsync(
                connection, transaction, matchId,
                foundItemId, "FOUND", cancellationToken);

            // Record why competing claims closed, while preserving their
            // original decision status and the confirmed match itself.
            await using (var deactivate = new MySqlCommand("""
                UPDATE matches
                SET is_active = 0,
                    deactivated_at = @now,
                    deactivation_reason = 'MATCH_CONFIRMED_ELSEWHERE',
                    deactivated_item_id =
                        CASE
                            WHEN lost_item_id = @lostId THEN @lostId
                            ELSE @foundId
                        END,
                    deactivated_item_type =
                        CASE
                            WHEN lost_item_id = @lostId THEN 'LOST'
                            ELSE 'FOUND'
                        END,
                    updated_at = @now
                WHERE id <> @matchId
                  AND is_active = 1
                  AND status IN (
                      'AWAITING_CLAIMANT_CONFIRMATION',
                      'LOST_REPORTER_CONFIRMED',
                      'FINDER_CONFIRMED'
                  )
                  AND (
                      lost_item_id = @lostId
                      OR found_item_id = @foundId
                  );
                """, connection, transaction))
            {
                deactivate.Parameters.AddWithValue("@matchId", matchId);
                deactivate.Parameters.AddWithValue("@lostId", lostItemId);
                deactivate.Parameters.AddWithValue("@foundId", foundItemId);
                deactivate.Parameters.AddWithValue("@now", now);

                await deactivate.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var cancel = new MySqlCommand("""
                UPDATE match_notifications AS n
                JOIN matches AS m ON m.id = n.match_id
                SET n.status = 'CANCELLED',
                    n.next_attempt_at = NULL,
                    n.lease_token = NULL,
                    n.lease_expires_at = NULL,
                    n.error_code = 'MATCH_INACTIVE',
                    n.updated_at = @now
                WHERE m.id <> @matchId
                  AND m.is_active = 0
                  AND (
                      m.lost_item_id = @lostId
                      OR m.found_item_id = @foundId
                  )
                  AND n.status IN ('PENDING', 'FAILED');
                """, connection, transaction);

            cancel.Parameters.AddWithValue("@matchId", matchId);
            cancel.Parameters.AddWithValue("@lostId", lostItemId);
            cancel.Parameters.AddWithValue("@foundId", foundItemId);
            cancel.Parameters.AddWithValue("@now", now);

            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new ClaimException(
                409,
                "One of these reports has already been confirmed in another match. Refresh the page.");
        }
        catch (MySqlException exception) when (exception.Number is 1205 or 1213)
        {
            throw new ClaimException(
                409,
                "Another match decision is being saved. Refresh and try again.");
        }
    }

    private static async Task BackfillPhotoAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Guid matchId,
        Guid itemId,
        string type,
        CancellationToken cancellationToken)
    {
        string? photoUrl;

        await using (var select = new MySqlCommand("""
            SELECT blob_url
            FROM image_descriptions
            WHERE item_id = @itemId
              AND item_type = @type
              AND is_superseded = 0
            ORDER BY created_at DESC, id DESC
            LIMIT 1;
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("@itemId", itemId);
            select.Parameters.AddWithValue("@type", type);

            photoUrl =
                await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        var column = type == "LOST" ? "lost_snapshot" : "found_snapshot";

        await using var update = new MySqlCommand($"""
            UPDATE matches
            SET {column} = JSON_SET(
                {column},
                '$.photoUrl', @photoUrl,
                '$.photoSnapshotCaptured', true
            )
            WHERE id = @id
              AND JSON_CONTAINS_PATH(
                  {column}, 'one', '$.photoSnapshotCaptured'
              ) = 0;
            """, connection, transaction);

        update.Parameters.AddWithValue("@id", matchId);
        update.Parameters.AddWithValue(
            "@photoUrl", (object?)photoUrl ?? DBNull.Value);

        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}