using MatchingService.Reevaluation;
using MatchingService.Tests.Integration;
using MySqlConnector;
using Xunit;

namespace MatchingService.Tests.Reevaluation;

/// <summary>
/// Story 8 tests proving MatchReevaluationEventHandler.HandleAsync's own topic/payload parsing
/// directly against real MySQL - no Kafka broker needed (see ItemCreatedEventKafkaIntegrationTests.cs
/// for the Kafka-wiring tests).
/// </summary>
[Collection("Docker Integration Tests 9")]
public sealed class MatchReevaluationEventHandlerTests : IClassFixture<ClaimServiceDbFixture>
{
    private readonly ClaimServiceDbFixture _fixture;
    private readonly MatchReevaluationEventHandler _handler;

    public MatchReevaluationEventHandlerTests(ClaimServiceDbFixture fixture)
    {
        _fixture = fixture;
        _handler = new MatchReevaluationEventHandler(new MatchReevaluationRepository(fixture.Connections, new()));
    }

    private static string ValidPayload(Guid eventId, Guid itemId, bool lost, string? title = "A brown wallet") =>
        $$"""
        {
            "eventId": "{{eventId}}",
            "timestamp": "{{DateTime.UtcNow:O}}",
            "userId": "{{Guid.NewGuid()}}",
            "{{(lost ? "lostItemId" : "foundItemId")}}": "{{itemId}}",
            "title": {{(title is null ? "null" : $"\"{title}\"")}},
            "category": "Accessories",
            "description": "A worn brown leather wallet.",
            "status": "ACTIVE",
            "dateLost": "2026-09-01",
            "lastKnownLocation": "Main Street",
            "photoUrls": []
        }
        """;

    private async Task<(string EventType, bool HasJob)> ReadSnapshotAsync(string itemType, Guid itemId)
    {
        await using var connection = _fixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            SELECT source_event_type FROM matching_item_snapshots
            WHERE item_type = @type AND item_id = @itemId;
            """, connection);
        command.Parameters.AddWithValue("@type", itemType);
        command.Parameters.AddWithValue("@itemId", itemId);

        var eventType = (string)(await command.ExecuteScalarAsync())!;

        await using var jobCommand = new MySqlCommand(
            "SELECT COUNT(*) FROM match_reevaluation_jobs WHERE item_id = @itemId;", connection);
        jobCommand.Parameters.AddWithValue("@itemId", itemId);

        var jobCount = (long)(await jobCommand.ExecuteScalarAsync())!;
        return (eventType, jobCount > 0);
    }

    [Fact]
    public async Task HandleAsync_LostItemUpdated_RecordsAnUpdatedSnapshotAndAJob()
    {
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await _handler.HandleAsync(
            "items.lost_item.updated", ValidPayload(eventId, itemId, lost: true), CancellationToken.None);

        var (eventType, hasJob) = await ReadSnapshotAsync("LOST", itemId);
        Assert.Equal("UPDATED", eventType);
        Assert.True(hasJob);
    }

    [Fact]
    public async Task HandleAsync_FoundItemUpdated_RecordsWithFoundItemType()
    {
        var itemId = Guid.NewGuid();

        await _handler.HandleAsync(
            "items.found_item.updated", ValidPayload(Guid.NewGuid(), itemId, lost: false), CancellationToken.None);

        var (eventType, hasJob) = await ReadSnapshotAsync("FOUND", itemId);
        Assert.Equal("UPDATED", eventType);
        Assert.True(hasJob);
    }

    [Fact]
    public async Task HandleAsync_LostItemCreated_RecordsACreatedSnapshotWithNoJob()
    {
        var itemId = Guid.NewGuid();

        await _handler.HandleAsync(
            "items.lost_item.created", ValidPayload(Guid.NewGuid(), itemId, lost: true), CancellationToken.None);

        var (eventType, hasJob) = await ReadSnapshotAsync("LOST", itemId);
        Assert.Equal("CREATED", eventType);
        Assert.False(hasJob);
    }

    [Fact]
    public async Task HandleAsync_MissingTitle_ThrowsInvalidDataException()
    {
        var itemId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidDataException>(() => _handler.HandleAsync(
            "items.lost_item.updated", ValidPayload(Guid.NewGuid(), itemId, lost: true, title: null),
            CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_UnsupportedTopic_ThrowsInvalidDataException()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _handler.HandleAsync(
            "items.auth_user.updated", ValidPayload(Guid.NewGuid(), Guid.NewGuid(), lost: true),
            CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_UnsupportedEventSuffix_ThrowsInvalidDataException()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _handler.HandleAsync(
            "items.lost_item.resolved", ValidPayload(Guid.NewGuid(), Guid.NewGuid(), lost: true),
            CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_EmptyPayload_ThrowsInvalidDataException()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _handler.HandleAsync(
            "items.lost_item.updated", "null", CancellationToken.None));
    }
}
