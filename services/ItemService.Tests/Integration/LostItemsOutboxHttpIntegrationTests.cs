using System.Net;
using System.Text.Json;
using ItemService.Models.Events;
using ItemService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

/// <summary>
/// Every other HTTP test in this suite runs against ItemServiceApiFactory's default wiring, which
/// replaces IEventPublisher with FakeEventPublisher (see ItemServiceApiFactory.ConfigureTestServices).
/// That is the right default for tests that only care about the HTTP contract, but it means none of
/// them prove that a real request actually goes:
///
///     Controller -> RequestTransactionFilter -> OutboxEventPublisher -> outbox_events
///
/// OutboxIntegrationTests proves the outbox mechanism works in isolation (publisher + relay against
/// a real IDbSession), but it calls OutboxEventPublisher directly - it never goes through the
/// controller or an HTTP request. This class closes that gap: it spins up a second host on the same
/// Testcontainers MySQL instance, with the real OutboxEventPublisher wired back in, and drives it
/// over HTTP.
/// </summary>
[Collection(ItemServiceIntegrationCollection.Name)]
public class LostItemsOutboxHttpIntegrationTests
{
    private readonly ItemServiceApiFactory _baseFactory;

    public LostItemsOutboxHttpIntegrationTests(ItemServiceApiFactory factory)
    {
        _baseFactory = factory;
        _ = _baseFactory.Services; // boots the collection-wide host once so MySQL + migrations are ready
    }

    /// <summary>
    /// A second host sharing the same disposable MySQL container (ConfigureWebHost on the base
    /// factory still runs, so _mysql/connection string come along for free) but with the real
    /// production publisher instead of FakeEventPublisher.
    /// </summary>
    private WebApplicationFactory<Program> CreateRealOutboxFactory(bool throwAfterOutboxWrite = false) =>
        _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEventPublisher>();

                if (throwAfterOutboxWrite)
                {
                    services.AddScoped<IEventPublisher, FaultInjectingOutboxEventPublisher>();
                }
                else
                {
                    services.AddScoped<IEventPublisher, OutboxEventPublisher>();
                }
            });
        });

    private async Task<MySqlConnection> OpenAsync()
    {
        var connection = new MySqlConnection(_baseFactory.GetTestDatabaseConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task Post_ValidForm_WritesTheOutboxEventThatBelongsToTheCreatedItem()
    {
        const string hidden = "http-real-outbox-hidden-value";

        using var factory = CreateRealOutboxFactory();
        using var client = TestAuthHelper.CreateClientWithValidCookie(factory);
        using var content = TestMultipartHelper.BuildValidForm(hidden);

        var response = await client.PostAsync("/api/items/lost", content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var itemId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        await using var connection = await OpenAsync();

        // The item really landed in lost_items ...
        await using (var itemCmd = new MySqlCommand(
            "SELECT COUNT(*) FROM lost_items WHERE id = @id;", connection))
        {
            itemCmd.Parameters.AddWithValue("@id", itemId.ToString());
            Assert.Equal(1L, Convert.ToInt64(await itemCmd.ExecuteScalarAsync()));
        }

        // ... and outbox_events has the matching event, written by the real OutboxEventPublisher,
        // on the very same request/transaction - not by a test calling the publisher directly.
        await using var eventCmd = new MySqlCommand(
            "SELECT payload FROM outbox_events WHERE topic = 'items.lost_item.created' AND payload LIKE @needle;",
            connection);
        eventCmd.Parameters.AddWithValue("@needle", $"%\"lostItemId\":\"{itemId}\"%");
        var payload = (string?)await eventCmd.ExecuteScalarAsync();

        Assert.NotNull(payload);
        Assert.Contains(hidden, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_WhenFailureHappensAfterTheOutboxRowIsWritten_RollsBackItemAndOutboxRowTogether()
    {
        // FaultInjectingOutboxEventPublisher lets the real OutboxEventPublisher write the outbox row
        // on the request's transaction, then throws - simulating "something later in the same
        // request fails". RequestTransactionFilter must roll the whole transaction back, so neither
        // the item nor the outbox row it already wrote may survive.
        const string hidden = "http-real-outbox-rollback-marker";

        using var factory = CreateRealOutboxFactory(throwAfterOutboxWrite: true);
        using var client = TestAuthHelper.CreateClientWithValidCookie(factory);
        using var content = TestMultipartHelper.BuildValidForm(hidden);

        var response = await client.PostAsync("/api/items/lost", content);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await using var connection = await OpenAsync();

        await using (var itemCmd = new MySqlCommand(
            "SELECT COUNT(*) FROM lost_items WHERE hidden_information = @hidden;", connection))
        {
            itemCmd.Parameters.AddWithValue("@hidden", hidden);
            Assert.Equal(0L, Convert.ToInt64(await itemCmd.ExecuteScalarAsync()));
        }

        await using (var eventCmd = new MySqlCommand(
            "SELECT COUNT(*) FROM outbox_events WHERE payload LIKE @needle;", connection))
        {
            eventCmd.Parameters.AddWithValue("@needle", $"%{hidden}%");
            Assert.Equal(0L, Convert.ToInt64(await eventCmd.ExecuteScalarAsync()));
        }
    }

    /// <summary>
    /// Wraps the real OutboxEventPublisher so the outbox INSERT genuinely happens on the request's
    /// transaction, then throws for the one marker this test uses. This is what proves atomicity
    /// through the real controller: a plain mock of IEventPublisher would never write a row at all,
    /// so it couldn't show that an already-written row gets rolled back too.
    /// </summary>
    private sealed class FaultInjectingOutboxEventPublisher : IEventPublisher
    {
        private readonly OutboxEventPublisher _inner;

        public FaultInjectingOutboxEventPublisher(ItemService.Databases.IDbSession session) =>
            _inner = new OutboxEventPublisher(session, NullLogger<OutboxEventPublisher>.Instance);

        public async ValueTask PublishAsync<T>(string topic, T payload, CancellationToken ct = default)
        {
            await _inner.PublishAsync(topic, payload, ct);

            if (payload is LostItemCreatedEvent { HiddenInformation: "http-real-outbox-rollback-marker" })
            {
                throw new InvalidOperationException("Simulated failure after the outbox row was written.");
            }
        }
    }
}
