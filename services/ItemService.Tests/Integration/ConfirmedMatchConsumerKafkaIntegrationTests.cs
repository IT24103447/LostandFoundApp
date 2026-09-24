using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using MySqlConnector;
using Xunit;

/// <summary>
/// Story 4/5 shared: proves a real Kafka message on the real "matches.confirmed" topic is actually
/// consumed by the real ConfirmedMatchConsumer and correctly resolves real item reports, over the real
/// Kafka wire protocol - not just that ConfirmedMatchHandler.HandleAsync produces the right result when
/// called directly (see ConfirmedMatchHandlerTests for that). The message is published here exactly the
/// way MatchConfirmationOutbox's real payload is shaped, confirmed independently in
/// MatchConfirmationKafkaIntegrationTests (Matching Service side) to genuinely reach a real broker.
/// </summary>
public sealed class ConfirmedMatchConsumerKafkaIntegrationTests : IClassFixture<ConfirmedMatchKafkaApiFactory>
{
    private readonly ConfirmedMatchKafkaApiFactory _factory;

    public ConfirmedMatchConsumerKafkaIntegrationTests(ConfirmedMatchKafkaApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> CreateLostItemAsync(HttpClient owner)
    {
        using var form = TestMultipartHelper.BuildValidForm("kafka-consumer-lost");
        var response = await owner.PostAsync("/api/items/lost", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateFoundItemAsync(HttpClient owner)
    {
        using var form = TestMultipartHelper.BuildValidFoundForm("kafka-consumer-found");
        var response = await owner.PostAsync("/api/items/found", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task PublishConfirmedAsync(
        Guid matchId, Guid lostItemId, Guid foundItemId, Guid lostReporterId, Guid finderId)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _factory.GetKafkaBootstrapAddress() }).Build();

        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventType = "match.confirmed",
            eventId = Guid.NewGuid(),
            matchId,
            lostItemId,
            foundItemId,
            lostReporterId,
            finderId,
            confirmedAt = DateTime.UtcNow
        });

        await producer.ProduceAsync(
            "matches.confirmed", new Message<string, string> { Key = matchId.ToString(), Value = payload });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private async Task<string> PollStatusAsync(string table, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new MySqlConnection(_factory.GetTestDatabaseConnectionStringForAssertions());
            await connection.OpenAsync();

            await using var command = new MySqlCommand($"SELECT status FROM {table} WHERE id = @id;", connection);
            command.Parameters.AddWithValue("@id", id.ToString());

            var status = (string?)await command.ExecuteScalarAsync();
            if (status == "RESOLVED") return status;

            await Task.Delay(500);
        }

        return "TIMED_OUT_WAITING_FOR_RESOLUTION";
    }

    // Story 4/5 shared: a real "matches.confirmed" message, produced independently of this process
    // (mirroring what the real MatchConfirmationPublisher actually sends), is consumed by the real
    // ConfirmedMatchConsumer over the real Kafka wire protocol and results in both real reports being
    // resolved - the one hop neither service's own direct-call tests can prove alone.
    [Fact]
    public async Task RealConfirmedMatchMessage_IsConsumedAndResolvesBothRealReports()
    {
        var lostOwnerId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        using var lostOwner = TestAuthHelper.CreateClientWithValidCookie(_factory, lostOwnerId);
        using var finder = TestAuthHelper.CreateClientWithValidCookie(_factory, finderId);

        var lostItemId = await CreateLostItemAsync(lostOwner);
        var foundItemId = await CreateFoundItemAsync(finder);

        await PublishConfirmedAsync(Guid.NewGuid(), lostItemId, foundItemId, lostOwnerId, finderId);

        Assert.Equal("RESOLVED", await PollStatusAsync("lost_items", lostItemId));
        Assert.Equal("RESOLVED", await PollStatusAsync("found_items", foundItemId));
    }
}
