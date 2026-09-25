using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Testcontainers.Kafka;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 7 (item lifecycle to match deactivation). Real, disposable MySQL (Testcontainers, via
/// ClaimServiceDbFixture) AND a real, disposable Kafka broker (Testcontainers) at once, deliberately -
/// the same justified exception MatchConfirmationKafkaIntegrationTests and
/// NotificationContactConsumerKafkaIntegrationTests already use, since ItemLifecycleConsumer's entire
/// job is relaying real Item Service events onto a real deactivation. ItemLifecycleRepository.ApplyAsync
/// itself is already thoroughly proven for real in ItemLifecycleRepositoryTests.cs; this file's job is
/// only to prove the real Kafka wiring (subscribe, consume, parse, dispatch, commit) - not to re-prove
/// ApplyAsync's own business logic.
/// </summary>
public sealed class ItemLifecycleConsumerKafkaIntegrationTests
    : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private readonly ClaimServiceDbFixture _dbFixture;
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    private const string LostTopic = "items.lost_item.resolved";
    private const string FoundTopic = "items.found_item.resolved";
    private const string DeleteTopic = "items.item.delete_requested";

    public ItemLifecycleConsumerKafkaIntegrationTests(ClaimServiceDbFixture dbFixture)
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
            new TopicSpecification { Name = LostTopic, NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = FoundTopic, NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = DeleteTopic, NumPartitions = 1, ReplicationFactor = 1 }
        ]);
    }

    public async Task DisposeAsync() => await _kafka.DisposeAsync();

    private sealed class FixedHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTestingKafka";
        public string ApplicationName { get; set; } = "MatchingService.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private ItemLifecycleConsumer CreateConsumer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
                ["Kafka:TopicPrefix"] = "items"
            })
            .Build();

        return new ItemLifecycleConsumer(
            new ItemLifecycleRepository(_dbFixture.Connections),
            configuration,
            new FixedHostEnvironment(),
            NullLogger<ItemLifecycleConsumer>.Instance);
    }

    private async Task<Guid> InsertMatchAsync(string status, Guid lostItemId, Guid foundItemId)
    {
        var id = Guid.NewGuid();
        var snapshot = """{"id":"11111111-1111-1111-1111-111111111111","type":"LOST","title":"t","category":"c","description":"d","date":"2026-09-01","location":"l"}""";

        await using var connection = _dbFixture.Connections.Create();
        await connection.OpenAsync();

        await using var command = new MySqlCommand("""
            INSERT INTO matches (
                id, lost_item_id, found_item_id, lost_reporter_id, finder_id, claimant_id,
                claimant_role, status, is_active, confidence_score, scoring_version,
                lost_snapshot, found_snapshot, created_at, updated_at
            ) VALUES (
                @id, @lostItemId, @foundItemId, @lostReporterId, @finderId, @lostReporterId,
                'LOST', @status, 1, 75.00, 'text-v1',
                @snapshot, @snapshot, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3)
            );
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@lostItemId", lostItemId);
        command.Parameters.AddWithValue("@foundItemId", foundItemId);
        command.Parameters.AddWithValue("@lostReporterId", Guid.NewGuid());
        command.Parameters.AddWithValue("@finderId", Guid.NewGuid());
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@snapshot", snapshot);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<bool?> WaitForDeactivationAsync(Guid matchId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var connection = _dbFixture.Connections.Create();
            await connection.OpenAsync();

            await using var command = new MySqlCommand(
                "SELECT is_active FROM matches WHERE id = @id;", connection);
            command.Parameters.AddWithValue("@id", matchId);

            var isActive = (bool)(await command.ExecuteScalarAsync())!;
            if (!isActive) return false;

            await Task.Delay(300);
        }

        return null;
    }

    private async Task PublishAsync(string topic, object payload)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        await producer.ProduceAsync(topic, new Message<string, string>
        {
            Value = JsonSerializer.Serialize(payload)
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RealLostItemResolvedEvent_DeactivatesTheRealActiveMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("LOST_REPORTER_CONFIRMED", lostItemId, foundItemId);

        var consumer = CreateConsumer();
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(LostTopic, new
            {
                eventId = Guid.NewGuid(),
                eventType = "lost_item.resolved",
                timestamp = DateTimeOffset.UtcNow,
                lostItemId
            });

            var isActive = await WaitForDeactivationAsync(matchId, TimeSpan.FromSeconds(30));
            Assert.False(isActive);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealFoundItemResolvedEvent_DeactivatesTheRealActiveMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("FINDER_CONFIRMED", lostItemId, foundItemId);

        var consumer = CreateConsumer();
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(FoundTopic, new
            {
                eventId = Guid.NewGuid(),
                eventType = "found_item.resolved",
                timestamp = DateTimeOffset.UtcNow,
                foundItemId
            });

            var isActive = await WaitForDeactivationAsync(matchId, TimeSpan.FromSeconds(30));
            Assert.False(isActive);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealItemDeleteRequestedEvent_DeactivatesTheRealActiveMatch()
    {
        var lostItemId = Guid.NewGuid();
        var foundItemId = Guid.NewGuid();
        var matchId = await InsertMatchAsync("AWAITING_CLAIMANT_CONFIRMATION", lostItemId, foundItemId);

        var consumer = CreateConsumer();
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync(DeleteTopic, new
            {
                eventId = Guid.NewGuid(),
                eventType = "item.delete_requested",
                timestamp = DateTimeOffset.UtcNow,
                itemId = lostItemId,
                itemType = "LOST"
            });

            var isActive = await WaitForDeactivationAsync(matchId, TimeSpan.FromSeconds(30));
            Assert.False(isActive);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }
}
