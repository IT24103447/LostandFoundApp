using AdminVerifyService.Databases;
using AdminVerifyService.Spam;

namespace AdminVerifyService.Listings;

public enum ListingEventOutcome
{
    Duplicate,
    AlreadyTracked,
    NewPost
}

public sealed class ListingEventHandler(
    IDbConnectionFactory connections,
    TrackedListingRepository listings,
    SpamRule spamRule,
    TimeProvider time)
{
    public async Task<ListingEventOutcome> HandleAsync(
        ListingEvent listingEvent,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await listings.MarkProcessedAsync(listingEvent, now, connection, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ListingEventOutcome.Duplicate;
        }

        if (!await listings.TrackAsync(listingEvent, now, connection, transaction, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return ListingEventOutcome.AlreadyTracked;
        }

        await spamRule.ApplyAsync(listingEvent, now, connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return ListingEventOutcome.NewPost;
    }
}
