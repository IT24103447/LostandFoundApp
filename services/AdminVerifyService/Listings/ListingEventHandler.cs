using AdminVerifyService.Databases;

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

        var outcome = await listings.TrackAsync(listingEvent, now, connection, transaction, cancellationToken)
            ? ListingEventOutcome.NewPost
            : ListingEventOutcome.AlreadyTracked;

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }
}
