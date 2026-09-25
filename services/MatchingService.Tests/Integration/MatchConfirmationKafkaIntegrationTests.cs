using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Configuration;
using MatchingService.Matches;
using MatchingService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Testcontainers.Kafka;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 4/5 shared (the outbox-to-Kafka publish half of the confirmation pipeline, used by both
/// LostReporterDecisionRepository and FinderDecisionRepository on confirm). Real, disposable MySQL
/// (Testcontainers, via ClaimServiceDbFixture) AND a real, disposable Kafka broker (Testcontainers) at
/// once, deliberately - the rest of this project avoids combining two real containers in one test (see
/// MatchingServiceKafkaApiFactory's own note), but MatchConfirmationPublisher's entire job is relaying
/// a row written to real MySQL onto a real broker, so there's no way to prove that relay genuinely
/// works without both being real. Classes are constructed directly, the same way ClaimServiceDbFixture-
/// based tests already do, rather than through a full WebApplicationFactory host.
/// </summary>
public sealed class MatchConfirmationKafkaIntegrationTests : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _dbFixture;
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    public MatchConfirmationKafkaIntegrationTests(ClaimServiceDbFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = MatchConfirmationOutbox.Topic, NumPartitions = 1, ReplicationFactor = 1
            }
        ]);
    }

    public async Task DisposeAsync() => await _kafka.DisposeAsync();

    // A fixed, non-Development environment name: skips EnsureDevelopmentTopicAsync's own polling entirely,
    // since the topic is already pre-created above, matching MatchingServiceKafkaApiFactory's convention.
    private sealed class FixedHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTestingKafka";
        public string ApplicationName { get; set; } = "MatchingService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private async Task<Guid> InsertFinderConfirmedMatchAsync(Guid lostReporterId, Guid finderId)
    {
        var id = Guid.NewGuid();
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        await using var connection = _dbFixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, finder_email, finder_phone, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @finderId,
                'FOUND', 'FINDER_CONFIRMED', 1, 75.00, 'text-v1',
                @snapshot, @snapshot, 'finder@example.com', '+94770000001', UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@lostReporterId", lostReporterId);
        command.Parameters.AddWithValue("@finderId", finderId);
        command.Parameters.AddWithValue("@snapshot", snapshot);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    // Story 4/5 shared: a match confirmed through either decision repository writes a real outbox row,
    // and the real MatchConfirmationPublisher (a genuine BackgroundService, not a mock) picks it up and
    // delivers it to a real Kafka broker - the one piece neither repository's own tests (real MySQL,
    // no Kafka) nor a mocked-producer test could prove on their own.
    [Fact]
    public async Task RealConfirmedMatch_IsRelayedByThePublisherToARealKafkaBroker()
    {
        var lostReporterId = Guid.NewGuid();
        var finderId = Guid.NewGuid();
        var matchId = await InsertFinderConfirmedMatchAsync(lostReporterId, finderId);

        var repository = new LostReporterDecisionRepository(_dbFixture.Connections, TimeProvider.System);
        await repository.DecideAsync(
            matchId, lostReporterId, confirm: true, CancellationToken.None,
            "lostreporter@example.com", "+94770000002");

        var store = new MatchConfirmationDeliveryStore(_dbFixture.Connections);
        var publisher = new MatchConfirmationPublisher(
            store,
            Options.Create(new KafkaSettings { BootstrapServers = _kafka.GetBootstrapAddress() }),
            new FixedHostEnvironment(),
            NullLogger<MatchConfirmationPublisher>.Instance);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = $"matching-service-tests-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe(MatchConfirmationOutbox.Topic);

        await publisher.StartAsync(CancellationToken.None);
        try
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(30));
            Assert.NotNull(result);

            using var payload = JsonDocument.Parse(result!.Message.Value);
            Assert.Equal(matchId, payload.RootElement.GetProperty("matchId").GetGuid());
            Assert.Equal("match.confirmed", payload.RootElement.GetProperty("eventType").GetString());
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
            consumer.Close();
        }
    }
}
