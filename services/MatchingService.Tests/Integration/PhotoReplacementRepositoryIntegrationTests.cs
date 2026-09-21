using MatchingService.Models;
using MatchingService.Repositories;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 1, Scenario 10 (photo replacement -> superseded description) integration tests. Proves the
/// repository's real SQL against a real, disposable MySQL database (Testcontainers) running the app's
/// own real migrations, including migration 003 that adds the superseded tracking columns.
/// When a replacement photo's description reaches COMPLETED, the prior description(s) of the same item
/// must be marked superseded, and GetLatestCurrentCompletedAsync must return only the latest
/// non-superseded completed description. Until the replacement completes, the original stays current.
/// This class has its own database fixture and every test completes or fails every row it creates,
/// so tests here never leave pending rows behind for each other to claim.
/// </summary>
public sealed class PhotoReplacementRepositoryIntegrationTests : IClassFixture<MatchingServiceDbApiFactory>
{
    private readonly MatchingServiceDbApiFactory _factory;
    private readonly IImageDescriptionRepository _repository;

    public PhotoReplacementRepositoryIntegrationTests(MatchingServiceDbApiFactory factory)
    {
        _factory = factory;
        _repository = factory.Services.GetRequiredService<IImageDescriptionRepository>();
    }

    private static ImageDescriptionRecord NewRecord(
        Guid itemId, ItemEventType eventType, DateTime occurredAt, ItemType itemType = ItemType.Lost)
    {
        var now = DateTime.UtcNow;
        return new ImageDescriptionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            eventType,
            occurredAt,
            Guid.NewGuid().ToString("N"), // photo_key: unique per row
            itemId,
            itemType,
            $"https://blob.example.com/{Guid.NewGuid()}.jpg",
            ImageProcessingStatus.Pending,
            0,
            null,
            now,
            now);
    }

    private async Task<ClaimedImageDescription> ClaimAsync(Guid id)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var claimed = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
            if (claimed is null)
            {
                break;
            }

            if (claimed.Id == id)
            {
                return claimed;
            }
        }

        throw new InvalidOperationException($"Row {id} was not claimable.");
    }

    private async Task CompleteNewRowAsync(ImageDescriptionRecord record, string description)
    {
        Assert.True(await _repository.CreatePendingAsync(record, CancellationToken.None));
        var claimed = await ClaimAsync(record.Id);
        Assert.True(await _repository.CompleteAsync(
            claimed,
            new GeneratedImageDescription { Description = description },
            "gemini-test-model",
            CancellationToken.None));
    }

    private async Task<(bool IsSuperseded, Guid? SupersededById, bool HasSupersededAt)> ReadSupersededAsync(Guid id)
    {
        await using var connection = new MySqlConnection(_factory.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new MySqlCommand(
            "SELECT is_superseded, superseded_by_id, superseded_at FROM image_descriptions WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            !reader.IsDBNull(2));
    }

    /* Scenario 10: once the replacement photo's description completes, the prior description of the same
       item is marked superseded and points at the replacement. The replacement becomes the current one. */
    [Fact]
    public async Task CompleteAsync_UpdatedEventRowCompleting_SupersedesPriorDescriptionAndBecomesCurrent()
    {
        var itemId = Guid.NewGuid();
        var original = NewRecord(itemId, ItemEventType.Created, DateTime.UtcNow.AddMinutes(-10));
        var replacement = NewRecord(itemId, ItemEventType.Updated, DateTime.UtcNow);

        await CompleteNewRowAsync(original, "Original photo: a red umbrella.");
        await CompleteNewRowAsync(replacement, "Replacement photo: a blue umbrella.");

        var originalState = await ReadSupersededAsync(original.Id);
        Assert.True(originalState.IsSuperseded);
        Assert.Equal(replacement.Id, originalState.SupersededById);
        Assert.True(originalState.HasSupersededAt);

        var replacementState = await ReadSupersededAsync(replacement.Id);
        Assert.False(replacementState.IsSuperseded);

        var current = await _repository.GetLatestCurrentCompletedAsync(itemId, ItemType.Lost, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(replacement.Id, current!.DescriptionId);
        Assert.Equal("Replacement photo: a blue umbrella.", current.Description);
    }

    /* Until the replacement's description completes, the original stays the current description.
       A pending replacement must not hide the original or leave the item with no description. */
    [Fact]
    public async Task GetLatestCurrentCompletedAsync_ReplacementStillPending_ReturnsOriginalDescription()
    {
        var itemId = Guid.NewGuid();
        var original = NewRecord(itemId, ItemEventType.Created, DateTime.UtcNow.AddMinutes(-10));
        var replacement = NewRecord(itemId, ItemEventType.Updated, DateTime.UtcNow);

        await CompleteNewRowAsync(original, "Original photo.");
        Assert.True(await _repository.CreatePendingAsync(replacement, CancellationToken.None));

        var current = await _repository.GetLatestCurrentCompletedAsync(itemId, ItemType.Lost, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(original.Id, current!.DescriptionId);
        Assert.False((await ReadSupersededAsync(original.Id)).IsSuperseded);

        // Leave nothing pending behind for other tests.
        var claimed = await ClaimAsync(replacement.Id);
        await _repository.RecordFailureAsync(claimed, "AI_REQUEST_FAILED", nextRetryAt: null, CancellationToken.None);
    }

    /* A replacement whose description fails permanently must not supersede anything.
       Superseding on failure would leave the item with no usable description at all. */
    [Fact]
    public async Task GetLatestCurrentCompletedAsync_ReplacementFailedPermanently_KeepsOriginalCurrent()
    {
        var itemId = Guid.NewGuid();
        var original = NewRecord(itemId, ItemEventType.Created, DateTime.UtcNow.AddMinutes(-10));
        var replacement = NewRecord(itemId, ItemEventType.Updated, DateTime.UtcNow);

        await CompleteNewRowAsync(original, "Original photo.");
        Assert.True(await _repository.CreatePendingAsync(replacement, CancellationToken.None));
        var claimed = await ClaimAsync(replacement.Id);
        Assert.True(await _repository.RecordFailureAsync(claimed, "AI_REQUEST_FAILED", nextRetryAt: null, CancellationToken.None));

        var current = await _repository.GetLatestCurrentCompletedAsync(itemId, ItemType.Lost, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(original.Id, current!.DescriptionId);
        Assert.False((await ReadSupersededAsync(original.Id)).IsSuperseded);
    }

    /* Two photos from the same created event are not replacements of each other.
       Completing a created-event row must never supersede another row. */
    [Fact]
    public async Task CompleteAsync_CreatedEventRowsForSameItem_DoNotSupersedeEachOther()
    {
        var itemId = Guid.NewGuid();
        var occurredAt = DateTime.UtcNow.AddMinutes(-5);
        var first = NewRecord(itemId, ItemEventType.Created, occurredAt);
        var second = NewRecord(itemId, ItemEventType.Created, occurredAt);

        await CompleteNewRowAsync(first, "First photo.");
        await CompleteNewRowAsync(second, "Second photo.");

        Assert.False((await ReadSupersededAsync(first.Id)).IsSuperseded);
        Assert.False((await ReadSupersededAsync(second.Id)).IsSuperseded);
        Assert.NotNull(await _repository.GetLatestCurrentCompletedAsync(itemId, ItemType.Lost, CancellationToken.None));
    }

    /* Descriptions can finish in a different order to the events that produced them, for example after
       retries. The newest event must still end up as the only current description, and an older
       description that completes late must be marked superseded by the newer one instead of becoming current. */
    [Fact]
    public async Task CompleteAsync_DescriptionsCompletingOutOfOrder_KeepsOnlyNewestCurrent()
    {
        var itemId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var created = NewRecord(itemId, ItemEventType.Created, now.AddMinutes(-30));
        var firstUpdate = NewRecord(itemId, ItemEventType.Updated, now.AddMinutes(-20));
        var secondUpdate = NewRecord(itemId, ItemEventType.Updated, now.AddMinutes(-10));

        foreach (var record in new[] { created, firstUpdate, secondUpdate })
        {
            Assert.True(await _repository.CreatePendingAsync(record, CancellationToken.None));
        }

        // Claims are handed out oldest event first, so all three are held before any completes.
        var claimedCreated = await ClaimAsync(created.Id);
        var claimedFirstUpdate = await ClaimAsync(firstUpdate.Id);
        var claimedSecondUpdate = await ClaimAsync(secondUpdate.Id);

        // Complete newest first, then the older ones.
        foreach (var (claimed, text) in new[]
                 {
                     (claimedSecondUpdate, "Second replacement."),
                     (claimedFirstUpdate, "First replacement."),
                     (claimedCreated, "Original.")
                 })
        {
            Assert.True(await _repository.CompleteAsync(
                claimed, new GeneratedImageDescription { Description = text }, "gemini-test-model", CancellationToken.None));
        }

        Assert.False((await ReadSupersededAsync(secondUpdate.Id)).IsSuperseded);

        var firstUpdateState = await ReadSupersededAsync(firstUpdate.Id);
        Assert.True(firstUpdateState.IsSuperseded);
        Assert.Equal(secondUpdate.Id, firstUpdateState.SupersededById);

        var createdState = await ReadSupersededAsync(created.Id);
        Assert.True(createdState.IsSuperseded);
        Assert.Equal(secondUpdate.Id, createdState.SupersededById);

        var current = await _repository.GetLatestCurrentCompletedAsync(itemId, ItemType.Lost, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(secondUpdate.Id, current!.DescriptionId);
    }

    // Superseding is scoped to one item. Replacing a photo on item X must leave item Y's description current.
    [Fact]
    public async Task CompleteAsync_ReplacementForOneItem_LeavesOtherItemsDescriptionCurrent()
    {
        var itemX = Guid.NewGuid();
        var itemY = Guid.NewGuid();
        var originalX = NewRecord(itemX, ItemEventType.Created, DateTime.UtcNow.AddMinutes(-10));
        var originalY = NewRecord(itemY, ItemEventType.Created, DateTime.UtcNow.AddMinutes(-10));
        var replacementX = NewRecord(itemX, ItemEventType.Updated, DateTime.UtcNow);

        await CompleteNewRowAsync(originalX, "Item X original.");
        await CompleteNewRowAsync(originalY, "Item Y original.");
        await CompleteNewRowAsync(replacementX, "Item X replacement.");

        Assert.True((await ReadSupersededAsync(originalX.Id)).IsSuperseded);
        Assert.False((await ReadSupersededAsync(originalY.Id)).IsSuperseded);

        var currentY = await _repository.GetLatestCurrentCompletedAsync(itemY, ItemType.Lost, CancellationToken.None);
        Assert.NotNull(currentY);
        Assert.Equal(originalY.Id, currentY!.DescriptionId);
    }

    // An item with no completed description has no current description, whether it is unknown or only pending.
    [Fact]
    public async Task GetLatestCurrentCompletedAsync_NoCompletedDescription_ReturnsNull()
    {
        var pendingItem = Guid.NewGuid();
        var pending = NewRecord(pendingItem, ItemEventType.Created, DateTime.UtcNow);
        Assert.True(await _repository.CreatePendingAsync(pending, CancellationToken.None));

        Assert.Null(await _repository.GetLatestCurrentCompletedAsync(Guid.NewGuid(), ItemType.Lost, CancellationToken.None));
        Assert.Null(await _repository.GetLatestCurrentCompletedAsync(pendingItem, ItemType.Lost, CancellationToken.None));

        // Leave nothing pending behind for other tests.
        var claimed = await ClaimAsync(pending.Id);
        await _repository.RecordFailureAsync(claimed, "AI_REQUEST_FAILED", nextRetryAt: null, CancellationToken.None);
    }

    /* Pending rows are claimed oldest source event first, whatever order they were inserted in.
       That keeps a replacement from being analysed before the photo it replaces. The dates are far in
       the past so these two rows sort ahead of any other row in the shared database. */
    [Fact]
    public async Task ClaimNextAsync_PendingRows_ClaimedInSourceOccurredAtOrder()
    {
        var itemId = Guid.NewGuid();
        var newer = NewRecord(itemId, ItemEventType.Updated, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var older = NewRecord(itemId, ItemEventType.Created, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // The newer event is inserted first on purpose.
        Assert.True(await _repository.CreatePendingAsync(newer, CancellationToken.None));
        Assert.True(await _repository.CreatePendingAsync(older, CancellationToken.None));

        var first = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);
        var second = await _repository.ClaimNextAsync(maxAttempts: 5, leaseSeconds: 60, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(older.Id, first!.Id);
        Assert.Equal(newer.Id, second!.Id);

        // Leave nothing claimed-but-unfinished behind for other tests.
        await _repository.RecordFailureAsync(first, "AI_REQUEST_FAILED", nextRetryAt: null, CancellationToken.None);
        await _repository.RecordFailureAsync(second, "AI_REQUEST_FAILED", nextRetryAt: null, CancellationToken.None);
    }
}
