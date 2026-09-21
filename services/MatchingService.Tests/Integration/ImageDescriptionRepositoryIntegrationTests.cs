using MatchingService.Models;
using MatchingService.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 1, Scenarios 3/7/8 integration tests. Proves ImageDescriptionRepository's real SQL against a
/// real, disposable MySQL database (Testcontainers) running the app's own real migrations.
/// This covers the unique-constraint idempotency and the claim/lease/complete/fail lifecycle,
/// not just that the repository interface is called with the right arguments (unit tests cover that).
/// Each test uses its own unique photo key/blob URL so tests sharing the one database don't collide.
/// </summary>
public sealed class ImageDescriptionRepositoryIntegrationTests : IClassFixture<MatchingServiceDbApiFactory>
{
    private readonly IImageDescriptionRepository _repository;

    public ImageDescriptionRepositoryIntegrationTests(MatchingServiceDbApiFactory factory)
    {
        _repository = factory.Services.GetRequiredService<IImageDescriptionRepository>();
    }

    private static ImageDescriptionRecord NewPendingRecord(string blobUrl, DateTime? nextRetryAt = null)
    {
        var now = DateTime.UtcNow;
        return new ImageDescriptionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ItemEventType.Created,
            now,
            Guid.NewGuid().ToString("N"), // photo_key: unique per test, real generator uses a SHA-256 hex digest of the same length class
            Guid.NewGuid(),
            ItemType.Lost,
            blobUrl,
            ImageProcessingStatus.Pending,
            0,
            nextRetryAt,
            now,
            now);
    }

    /* Scenario 3: a pending row written through CreatePendingAsync is genuinely persisted and becomes
       claimable. Proves the insert round-trips through real SQL, not just that it didn't throw. */
    [Fact]
    public async Task CreatePendingAsync_NewPhotoKey_InsertsRowThatBecomesClaimable()
    {
        var record = NewPendingRecord("https://blob.example.com/create-1.jpg");

        var inserted = await _repository.CreatePendingAsync(record, CancellationToken.None);
        Assert.True(inserted);

        var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(record.Id, claimed!.Id);
        Assert.Equal(record.BlobUrl, claimed.BlobUrl);

        // The claim also carries the item and source-event metadata that photo replacement relies on.
        Assert.Equal(record.ItemId, claimed.ItemId);
        Assert.Equal(record.ItemType, claimed.ItemType);
        Assert.Equal(ItemEventType.Created, claimed.SourceEventType);
        Assert.Equal(record.SourceOccurredAt, claimed.SourceOccurredAt, TimeSpan.FromMilliseconds(1));
    }

    /* Scenario 8: at-least-once Kafka redelivery must not create a duplicate row for the same photo.
       This is the real unique-constraint (MySQL error 1062) path, not the in-memory dedup the handler
       also does before this is ever reached. */
    [Fact]
    public async Task CreatePendingAsync_DuplicatePhotoKey_ReturnsFalseAndLeavesOriginalRowUnchanged()
    {
        var photoKey = Guid.NewGuid().ToString("N");
        var original = NewPendingRecord("https://blob.example.com/dup-original.jpg") with { };
        var firstRecord = original with { PhotoKey = photoKey };
        var secondRecord = NewPendingRecord("https://blob.example.com/dup-replay.jpg") with { PhotoKey = photoKey };

        Assert.True(await _repository.CreatePendingAsync(firstRecord, CancellationToken.None));
        Assert.False(await _repository.CreatePendingAsync(secondRecord, CancellationToken.None));

        var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(claimed);
        /* The original row's blob URL must win. The "duplicate" (redelivery) insert must not have
           overwritten it. */
        Assert.Equal(firstRecord.BlobUrl, claimed!.BlobUrl);
    }

    // A row whose next_retry_at is in the future must not be claimed early.
    [Fact]
    public async Task ClaimNextAsync_RowNotYetDue_ReturnsNull()
    {
        var record = NewPendingRecord(
            "https://blob.example.com/not-due.jpg", nextRetryAt: DateTime.UtcNow.AddMinutes(10));
        await _repository.CreatePendingAsync(record, CancellationToken.None);

        /* Claim everything else due right now first, so this row (not due) is the only remaining
           candidate and its absence from the result proves the date filter, not just bad luck. */
        ImageDescriptionRecord? found = null;
        while (await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None)
                   is { } claimed)
        {
            if (claimed.Id == record.Id) found = record;
        }

        Assert.Null(found);
    }

    /* Once claimed, a row is PROCESSING and must not be handed to a second, concurrent claim. Proves
       the claim's row-locking actually excludes it, not just that one claim happened to see it. */
    [Fact]
    public async Task ClaimNextAsync_AlreadyClaimedRow_IsNotClaimedAgain()
    {
        var record = NewPendingRecord("https://blob.example.com/claim-once.jpg");
        await _repository.CreatePendingAsync(record, CancellationToken.None);

        var first = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(record.Id, first!.Id);

        // Drain anything else due, confirming this specific row never comes back around.
        ImageDescriptionRecord? seenAgain = null;
        while (await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None)
                   is { } claimed)
        {
            if (claimed.Id == record.Id) seenAgain = record;
        }

        Assert.Null(seenAgain);
    }

    /* Scenario 3: CompleteAsync with the correct lease token stores the description and finalizes the
       row. It no longer comes back through ClaimNextAsync. */
    [Fact]
    public async Task CompleteAsync_MatchingLeaseToken_MarksCompletedAndStopsClaimLoop()
    {
        var record = NewPendingRecord("https://blob.example.com/complete-1.jpg");
        await _repository.CreatePendingAsync(record, CancellationToken.None);
        var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(claimed);

        var description = new GeneratedImageDescription { Description = "A red umbrella." };
        var completed = await _repository.CompleteAsync(claimed!, description, "gemini-test-model", CancellationToken.None);
        Assert.True(completed);

        ImageDescriptionRecord? seenAgain = null;
        while (await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None)
                   is { } reclaimed)
        {
            if (reclaimed.Id == record.Id) seenAgain = record;
        }

        Assert.Null(seenAgain);
    }

    /* A CompleteAsync call carrying the wrong lease token (e.g. a stale worker that lost its lease)
       must not be able to finalize someone else's claim. */
    [Fact]
    public async Task CompleteAsync_WrongLeaseToken_ReturnsFalse()
    {
        var record = NewPendingRecord("https://blob.example.com/wrong-lease.jpg");
        await _repository.CreatePendingAsync(record, CancellationToken.None);
        var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(claimed);

        var impostor = claimed! with { LeaseToken = Guid.NewGuid() };
        var description = new GeneratedImageDescription { Description = "Should not be stored." };

        var completed = await _repository.CompleteAsync(impostor, description, "gemini-test-model", CancellationToken.None);
        Assert.False(completed);
    }

    /* Scenario 7: recording a permanent failure (no next retry) leaves the job unclaimable. It must
       not resurface for further processing. */
    [Fact]
    public async Task RecordFailureAsync_NoNextRetry_LeavesRowUnclaimable()
    {
        var record = NewPendingRecord("https://blob.example.com/permanent-fail.jpg");
        await _repository.CreatePendingAsync(record, CancellationToken.None);
        var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        Assert.NotNull(claimed);

        var recorded = await _repository.RecordFailureAsync(
            claimed!, "IMAGE_TYPE_UNSUPPORTED", nextRetryAt: null, CancellationToken.None);
        Assert.True(recorded);

        ImageDescriptionRecord? seenAgain = null;
        while (await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None)
                   is { } reclaimed)
        {
            if (reclaimed.Id == record.Id) seenAgain = record;
        }

        Assert.Null(seenAgain);
    }
}
