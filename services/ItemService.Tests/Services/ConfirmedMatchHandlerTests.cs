using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ItemService.Models.Events;
using ItemService.Services;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

/// <summary>
/// Story 4/5 (lost reporter and finder match decisions) contract tests for ConfirmedMatchHandler, the
/// consumer-side half of the outbox pipeline Matching Service's MatchConfirmationOutbox feeds
/// (topic "matches.confirmed"). ConfirmedMatchConsumer itself is a BackgroundService wrapping a Kafka
/// poll loop - ItemServiceApiFactory's ConfigureTestServices removes every IHostedService, so it never
/// runs here - these tests call HandleAsync directly instead, resolved from the same DI container real
/// requests use, matching MarkItemResolvedApiIntegrationTests' real-database style. Lost/found reports
/// are created through the real HTTP endpoints, so their owner ids and initial ACTIVE status are real,
/// not asserted assumptions.
/// </summary>
[Collection(ItemServiceIntegrationCollection.Name)]
public sealed class ConfirmedMatchHandlerTests
{
    private readonly ItemServiceApiFactory _factory;

    public ConfirmedMatchHandlerTests(ItemServiceApiFactory factory) => _factory = factory;

    private async Task<Guid> CreateLostItemAsync(HttpClient owner, string hidden = "confirmed-match-lost")
    {
        using var form = TestMultipartHelper.BuildValidForm(hidden);
        var response = await owner.PostAsync("/api/items/lost", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateFoundItemAsync(HttpClient owner, string hidden = "confirmed-match-found")
    {
        using var form = TestMultipartHelper.BuildValidFoundForm(hidden);
        var response = await owner.PostAsync("/api/items/found", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string> GetStatusAsync(string table, Guid id)
    {
        await using var connection = new MySqlConnection(_factory.GetTestDatabaseConnectionString());
        await connection.OpenAsync();

        await using var command = new MySqlCommand($"SELECT status FROM {table} WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", id.ToString());

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static MatchConfirmedIntegrationEvent NewEvent(
        Guid lostItemId, Guid foundItemId, Guid lostReporterId, Guid finderId, Guid? eventId = null) =>
        new(
            SchemaVersion: 1,
            EventType: "match.confirmed",
            EventId: eventId ?? Guid.NewGuid(),
            MatchId: Guid.NewGuid(),
            LostItemId: lostItemId,
            FoundItemId: foundItemId,
            LostReporterId: lostReporterId,
            FinderId: finderId,
            ConfirmedAt: DateTime.UtcNow);

    private async Task HandleAsync(MatchConfirmedIntegrationEvent message)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ConfirmedMatchHandler>()
            .HandleAsync(message, CancellationToken.None);
    }

    // The happy path: both reports resolve and each publishes its own resolved event, exactly the way
    // MarkItemResolvedApiIntegrationTests proves the direct /resolve endpoint does.
    [Fact]
    public async Task HandleAsync_ValidEvent_ResolvesBothReportsAndPublishesResolvedEvents()
    {
        var lostOwnerId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);
        _factory.FakeEvents.Clear();

        await HandleAsync(NewEvent(lostItemId, foundItemId, lostOwnerId, finderId));

        Assert.Equal("RESOLVED", await GetStatusAsync("lost_items", lostItemId));
        Assert.Equal("RESOLVED", await GetStatusAsync("found_items", foundItemId));

        Assert.True(_factory.FakeEvents.WasPublishedTo("lost_item.resolved"));
        Assert.True(_factory.FakeEvents.WasPublishedTo("found_item.resolved"));
        Assert.Equal(2, _factory.FakeEvents.Published.Count);
    }

    // The inbox's own INSERT IGNORE guard: redelivering the identical event (a crash between the Kafka
    // write and the outbox commit can resend it) must not resolve anything or publish a second time.
    [Fact]
    public async Task HandleAsync_DuplicateEventId_IsIdempotentAndPublishesOnlyOnce()
    {
        var lostOwnerId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);
        _factory.FakeEvents.Clear();

        var message = NewEvent(lostItemId, foundItemId, lostOwnerId, finderId);
        await HandleAsync(message);
        await HandleAsync(message);

        Assert.Equal(2, _factory.FakeEvents.Published.Count);
    }

    // A report already resolved by some other path (e.g. the owner resolved it manually first) is left
    // alone rather than re-resolved or re-published - only the still-ACTIVE side of the pair republishes.
    [Fact]
    public async Task HandleAsync_LostReportAlreadyResolved_SkipsItsEventButStillResolvesFoundSide()
    {
        var lostOwnerId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);

        var resolved = await lostOwner.PostAsync($"/api/items/lost/{lostItemId}/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        _factory.FakeEvents.Clear();

        await HandleAsync(NewEvent(lostItemId, foundItemId, lostOwnerId, finderId));

        Assert.Equal("RESOLVED", await GetStatusAsync("found_items", foundItemId));
        Assert.False(_factory.FakeEvents.WasPublishedTo("lost_item.resolved"));
        Assert.True(_factory.FakeEvents.WasPublishedTo("found_item.resolved"));
        Assert.Single(_factory.FakeEvents.Published);
    }

    // The event's claimed reporter must match the report's real owner - a mismatch fails the whole
    // pair closed rather than resolving one side on unverified data.
    [Fact]
    public async Task HandleAsync_LostReporterIdDoesNotMatchRealOwner_FailsWithoutMutatingEitherReport()
    {
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);
        _factory.FakeEvents.Clear();

        await HandleAsync(NewEvent(lostItemId, foundItemId, Guid.NewGuid(), finderId));

        Assert.Equal("ACTIVE", await GetStatusAsync("lost_items", lostItemId));
        Assert.Equal("ACTIVE", await GetStatusAsync("found_items", foundItemId));
        Assert.Empty(_factory.FakeEvents.Published);
    }

    // A soft-deleted report can't be resolved into a confirmed match.
    [Fact]
    public async Task HandleAsync_FoundReportDeleted_FailsWithoutMutatingLostReport()
    {
        var lostOwnerId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);

        Assert.Equal(HttpStatusCode.OK, (await finder.DeleteAsync($"/api/items/found/{foundItemId}")).StatusCode);
        _factory.FakeEvents.Clear();

        await HandleAsync(NewEvent(lostItemId, foundItemId, lostOwnerId, finderId));

        Assert.Equal("ACTIVE", await GetStatusAsync("lost_items", lostItemId));
        Assert.Empty(_factory.FakeEvents.Published);
    }

    // An event referencing a report id that was never created at all fails the same closed way.
    [Fact]
    public async Task HandleAsync_UnknownReportId_FailsWithoutPublishingAnyEvent()
    {
        var lostOwnerId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        var lostItemId = await CreateLostItemAsync(lostOwner);
        _factory.FakeEvents.Clear();

        await HandleAsync(NewEvent(lostItemId, Guid.NewGuid(), lostOwnerId, Guid.NewGuid()));

        Assert.Equal("ACTIVE", await GetStatusAsync("lost_items", lostItemId));
        Assert.Empty(_factory.FakeEvents.Published);
    }

    // A malformed event (here, the same id on both sides) is rejected before any database work happens.
    [Fact]
    public async Task HandleAsync_InvalidEvent_ThrowsInvalidDataException()
    {
        var itemId = Guid.NewGuid();
        var message = NewEvent(itemId, itemId, Guid.NewGuid(), Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidDataException>(() => HandleAsync(message));
    }
}
