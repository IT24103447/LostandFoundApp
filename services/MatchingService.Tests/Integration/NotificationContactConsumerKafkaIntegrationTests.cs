using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Testcontainers.Kafka;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Story 6 (email notifications). Real, disposable MySQL (Testcontainers, via ClaimServiceDbFixture) AND
/// a real, disposable Kafka broker (Testcontainers) at once, deliberately - the same justified exception
/// MatchConfirmationKafkaIntegrationTests already uses, since NotificationContactConsumer's entire job is
/// relaying real Auth Service events onto a real local cache table (notification_contacts). Proves the
/// "last event wins" ordering logic and each topic's own effect (Gap #3 in Bugs_Sprint3.md records that
/// this Kafka-cache mechanism, not an Auth Service HTTP endpoint, is what recipient resolution actually
/// relies on).
/// </summary>
public sealed class NotificationContactConsumerKafkaIntegrationTests
    : IClassFixture<ClaimServiceDbFixture>, IAsyncLifetime
{
    private static readonly string[] Topics =
    [
        "auth.user.verified",
        "auth.user.profile_updated",
        "auth.user.kicked",
        "auth.user.unkicked",
        "auth.user.deleted"
    ];

    private readonly ClaimServiceDbFixture _dbFixture;
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    public NotificationContactConsumerKafkaIntegrationTests(ClaimServiceDbFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        await admin.CreateTopicsAsync(
            Topics.Select(topic => new TopicSpecification
            {
                Name = topic, NumPartitions = 1, ReplicationFactor = 1
            }));
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

    private MatchingService.Notifications.NotificationContactConsumer CreateConsumer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress()
            })
            .Build();

        return new MatchingService.Notifications.NotificationContactConsumer(
            _dbFixture.Connections,
            configuration,
            new FixedHostEnvironment(),
            NullLogger<MatchingService.Notifications.NotificationContactConsumer>.Instance);
    }

    private async Task PublishAsync(string topic, Guid userId, string email, DateTime timestamp)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        var payload = JsonSerializer.Serialize(new
        {
            userId,
            email,
            timestamp = timestamp.ToString("O")
        });

        await producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = userId.ToString(),
            Value = payload
        });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private async Task<(bool Found, string? Email, bool IsActive, bool IsDeleted)> WaitForContactAsync(
        Guid userId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            await using var connection = _dbFixture.Connections.Create();
            await connection.OpenAsync();

            await using var command = new MySqlCommand(
                "SELECT email, is_active, is_deleted FROM notification_contacts WHERE user_id = @userId;",
                connection);
            command.Parameters.AddWithValue("@userId", userId);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (true, reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2));
            }

            await Task.Delay(500);
        }

        return (false, null, false, false);
    }

    [Fact]
    public async Task RealVerifiedEvent_CreatesAnActiveNotificationContactRow()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "user@example.com", DateTime.UtcNow);

            var (found, email, isActive, isDeleted) =
                await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));

            Assert.True(found, "Expected a notification_contacts row to appear after a real Kafka event.");
            Assert.Equal("user@example.com", email);
            Assert.True(isActive);
            Assert.False(isDeleted);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealProfileUpdatedEvent_NewerThanExisting_UpdatesTheEmail()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();
        var earlier = DateTime.UtcNow.AddMinutes(-5);

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "old@example.com", earlier);
            await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));

            await PublishAsync("auth.user.profile_updated", userId, "new@example.com", DateTime.UtcNow);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            (bool Found, string? Email, bool IsActive, bool IsDeleted) result = default;
            while (DateTime.UtcNow < deadline)
            {
                result = await WaitForContactAsync(userId, TimeSpan.FromSeconds(2));
                if (result.Email == "new@example.com") break;
            }

            Assert.Equal("new@example.com", result.Email);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealProfileUpdatedEvent_OlderThanExisting_IsIgnored()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "current@example.com", now);
            await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));

            // A stale/out-of-order redelivery of an OLDER event must never overwrite newer data.
            await PublishAsync("auth.user.profile_updated", userId, "stale@example.com", now.AddMinutes(-10));

            // No positive wait to prove absence deterministically - give the (incorrect) update every
            // chance to land, then confirm it didn't.
            await Task.Delay(TimeSpan.FromSeconds(5));
            var (_, email, _, _) = await WaitForContactAsync(userId, TimeSpan.FromSeconds(5));

            Assert.Equal("current@example.com", email);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealKickedEvent_MarksTheContactInactive()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "user@example.com", now);
            await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));

            await PublishAsync("auth.user.kicked", userId, "user@example.com", now.AddSeconds(1));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            (bool Found, string? Email, bool IsActive, bool IsDeleted) result = default;
            while (DateTime.UtcNow < deadline)
            {
                result = await WaitForContactAsync(userId, TimeSpan.FromSeconds(2));
                if (!result.IsActive) break;
            }

            Assert.False(result.IsActive);
            Assert.False(result.IsDeleted);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealUnkickedEvent_ReactivatesTheContact()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "user@example.com", now);
            await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));
            await PublishAsync("auth.user.kicked", userId, "user@example.com", now.AddSeconds(1));

            var kickedDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < kickedDeadline &&
                   (await WaitForContactAsync(userId, TimeSpan.FromSeconds(2))).IsActive)
            {
            }

            await PublishAsync("auth.user.unkicked", userId, "user@example.com", now.AddSeconds(2));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            (bool Found, string? Email, bool IsActive, bool IsDeleted) result = default;
            while (DateTime.UtcNow < deadline)
            {
                result = await WaitForContactAsync(userId, TimeSpan.FromSeconds(2));
                if (result.IsActive) break;
            }

            Assert.True(result.IsActive);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RealDeletedEvent_MarksTheContactDeletedAndInactive()
    {
        var consumer = CreateConsumer();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await PublishAsync("auth.user.verified", userId, "user@example.com", now);
            await WaitForContactAsync(userId, TimeSpan.FromSeconds(30));

            await PublishAsync("auth.user.deleted", userId, "user@example.com", now.AddSeconds(1));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            (bool Found, string? Email, bool IsActive, bool IsDeleted) result = default;
            while (DateTime.UtcNow < deadline)
            {
                result = await WaitForContactAsync(userId, TimeSpan.FromSeconds(2));
                if (result.IsDeleted) break;
            }

            Assert.True(result.IsDeleted);
            Assert.False(result.IsActive);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }
}
