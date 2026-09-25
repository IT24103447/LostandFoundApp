// changed during sprint 3 by dev
using ItemService.Configuration;
using ItemService.Databases;
using ItemService.Models;
using ItemService.Models.Events;
using ItemService.Repositories;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace ItemService.Services;

public sealed class ConfirmedMatchHandler(
    IDbSession session,
    ILostItemsRepository lostItems,
    IFoundItemsRepository foundItems,
    IEventPublisher publisher,
    IOptions<KafkaSettings> kafka,
    ILogger<ConfirmedMatchHandler> logger)
{
    public async Task HandleAsync(MatchConfirmedIntegrationEvent message, CancellationToken ct)
    {
        if (!message.IsValid()) throw new InvalidDataException("Invalid match confirmation event.");

        session.BeginRequestTransaction();
        try
        {
            await using var lease = await session.AcquireAsync(ct);
            await using (var insert = new MySqlCommand("""
                INSERT IGNORE INTO match_resolution_inbox
                    (event_id, match_id, lost_item_id, found_item_id, status, received_at)
                VALUES (@eventId, @matchId, @lostId, @foundId, 'PROCESSING', @now);
                """, lease.Connection, lease.Transaction))
            {
                insert.Parameters.AddWithValue("@eventId", message.EventId);
                insert.Parameters.AddWithValue("@matchId", message.MatchId);
                insert.Parameters.AddWithValue("@lostId", message.LostItemId);
                insert.Parameters.AddWithValue("@foundId", message.FoundItemId);
                insert.Parameters.AddWithValue("@now", DateTime.UtcNow);
                if (await insert.ExecuteNonQueryAsync(ct) == 0)
                {
                    await session.CommitAsync(ct);
                    return;
                }
            }

            // Always lock lost then found. Validate both before changing either report.
            var lostState = await LockReportAsync(lease, "lost_items", message.LostItemId, ct);
            var foundState = await LockReportAsync(lease, "found_items", message.FoundItemId, ct);
            var error = Validate(lostState, message.LostReporterId)
                ?? Validate(foundState, message.FinderId);

            if (error is not null)
            {
                await FinishAsync(lease, message.EventId, "FAILED", error, ct);
                await session.CommitAsync(ct);
                logger.LogError("Match {MatchId} could not resolve reports. Code: {Code}.", message.MatchId, error);
                return;
            }

            var lost = await lostItems.GetByIdAsync(message.LostItemId, ct)
                ?? throw new InvalidOperationException("Locked lost report could not be read.");
            var found = await foundItems.GetByIdAsync(message.FoundItemId, ct)
                ?? throw new InvalidOperationException("Locked found report could not be read.");
            var now = DateTime.UtcNow;

            if (lostState!.Status == "ACTIVE")
            {
                await lostItems.UpdateStatusAsync(lost.Id, LostItemStatus.RESOLVED, now, ct);
                await publisher.PublishAsync($"{kafka.Value.TopicPrefix}.lost_item.resolved",
                    new LostItemResolvedEvent
                    {
                        UserId = lost.UserId,
                        LostItemId = lost.Id,
                        Title = lost.Title,
                        Category = lost.Category,
                        Description = lost.Description,
                        DateLost = lost.DateLost,
                        LastKnownLocation = lost.LastKnownLocation,
                        HiddenInformation = lost.HiddenInformation,
                        Status = LostItemStatus.RESOLVED.ToString(),
                        PhotoUrls = lost.Photos.Select(photo => photo.Url).ToList(),
                        ResolvedAt = now
                    }, ct);
            }

            if (foundState!.Status == "ACTIVE")
            {
                await foundItems.UpdateStatusAsync(found.Id, FoundItemStatus.RESOLVED, now, ct);
                await publisher.PublishAsync($"{kafka.Value.TopicPrefix}.found_item.resolved",
                    new FoundItemResolvedEvent
                    {
                        UserId = found.UserId,
                        FoundItemId = found.Id,
                        Title = found.Title,
                        Category = found.Category,
                        Description = found.Description,
                        DateFound = found.DateFound,
                        LocationFound = found.LocationFound,
                        HiddenInformation = found.HiddenInformation,
                        Status = FoundItemStatus.RESOLVED.ToString(),
                        PhotoUrls = found.Photos.Select(photo => photo.Url).ToList(),
                        ResolvedAt = now
                    }, ct);
            }

            // Item writes, outgoing resolved events and duplicate protection commit together.
            await FinishAsync(lease, message.EventId, "COMPLETED", null, ct);
            await session.CommitAsync(ct);
            logger.LogInformation("Resolved reports for confirmed match {MatchId}.", message.MatchId);
        }
        catch
        {
            await session.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private sealed record ReportState(Guid OwnerId, string Status, bool Deleted);

    private static async Task<ReportState?> LockReportAsync(
        DbLease lease, string table, Guid id, CancellationToken ct)
    {
        // The table argument is supplied only by the two fixed calls above.
        await using var command = new MySqlCommand($"""
            SELECT user_id, status, deleted_at FROM {table} WHERE id = @id FOR UPDATE;
            """, lease.Connection, lease.Transaction);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ReportState(reader.GetGuid(0), reader.GetString(1), !reader.IsDBNull(2))
            : null;
    }

    private static string? Validate(ReportState? state, Guid expectedOwner)
    {
        if (state is null) return "REPORT_NOT_FOUND";
        if (state.Deleted) return "REPORT_DELETED";
        if (state.OwnerId != expectedOwner) return "REPORT_OWNER_MISMATCH";
        if (state.Status is not ("ACTIVE" or "RESOLVED")) return "REPORT_STATE_CONFLICT";
        return null;
    }

    private static async Task FinishAsync(
        DbLease lease, Guid eventId, string status, string? error, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            UPDATE match_resolution_inbox
            SET status = @status, error_code = @error, processed_at = @now
            WHERE event_id = @id;
            """, lease.Connection, lease.Transaction);
        command.Parameters.AddWithValue("@id", eventId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }
}
