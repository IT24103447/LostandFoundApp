using AdminVerifyService.Configuration;
using AdminVerifyService.Listings;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AdminVerifyService.Spam;

public enum SpamRuleOutcome
{
    NotFlagged,
    JoinedRecord,
    CreatedRecord
}

public sealed class SpamRule(
    SpamRecordRepository records,
    IOptionsMonitor<DetectionSettings> settings,
    ILogger<SpamRule> logger)
{
    public async Task<SpamRuleOutcome> ApplyAsync(
        ListingEvent listing,
        DateTime now,
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var detection = settings.CurrentValue;
        var collecting = await records.LockCollectingAsync(listing.UserId, connection, transaction, cancellationToken);

        if (collecting is not null)
        {
            if (CanJoin(collecting, listing, detection))
            {
                await records.AddListingAsync(collecting.Id, listing.ListingId, now, connection, transaction, cancellationToken);
                return SpamRuleOutcome.JoinedRecord;
            }

            await records.StopCollectingAsync(collecting.Id, now, connection, transaction, cancellationToken);
        }

        var window = TimeSpan.FromMinutes(detection.SpamWindowMinutes);

        var freeListings = await records.FindFreeListingsAsync(
            listing, window, detection.SpamRecordCap, connection, transaction, cancellationToken);

        if (freeListings.Count < detection.SpamThreshold)
        {
            return SpamRuleOutcome.NotFlagged;
        }

        var recordId = Guid.NewGuid();

        var queuedEmails = await records.CreateAsync(
            recordId, listing, freeListings, listing.PostedAt + window, now, connection, transaction, cancellationToken);

        logger.LogInformation(
            "Created spam record {RecordId} with {ListingCount} listings. Queued {EmailCount} admin emails.",
            recordId, freeListings.Count, queuedEmails);

        return SpamRuleOutcome.CreatedRecord;
    }

    private static bool CanJoin(
        CollectingRecord record,
        ListingEvent listing,
        DetectionSettings detection) =>
        record.Status == SpamRecordStatus.NeedsReview &&
        listing.PostedAt < record.CollectingUntil &&
        (detection.SpamRecordCap == 0 || record.ScoreA < detection.SpamRecordCap);
}
