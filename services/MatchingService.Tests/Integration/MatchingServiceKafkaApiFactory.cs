using System.Collections.Generic;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Databases;
using MatchingService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using MySqlConnector;
using Testcontainers.Kafka;
using Testcontainers.MySql;
using MatchingService.Tests.Support;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Real Kafka broker AND real MySQL (both Testcontainers), wired to the real ItemCreatedEventConsumer.
/// IImageDescriptionRepository is mocked; MatchReevaluationEventHandler's own repository is real, since
/// it opens a real MySQL connection on every message regardless of topic.
/// </summary>
public sealed class MatchingServiceKafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("matching_service")
        .WithUsername("matching_service_test")
        .WithPassword("test_password")
        .Build();

    public Mock<IImageDescriptionRepository> Repository { get; } = new();

    public string GetBootstrapAddress() => _kafka.GetBootstrapAddress();

    public MySqlConnection OpenDbConnection()
    {
        var connection = new MySqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await Task.WhenAll(_kafka.StartAsync(), _mysql.StartAsync());

        _connectionString = new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

        /* Migrations run directly (ClaimServiceDbFixture's pattern), not via "Development" - that would
           also flip on every other hosted consumer's own dev-only topic auto-creation. */
        var migrationConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = _connectionString
            })
            .Build();

        var migrationServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(migrationConfiguration)
            .AddSingleton<IDbConnectionFactory, DbConnectionFactory>()
            .BuildServiceProvider();

        DbInitializer.RunPendingMigrations(migrationServices, migrationConfiguration);

        /* Create all four topics explicitly before the consumer ever subscribes (the created and updated
           topics for both lost and found items). Relying on lazy
           auto-creation racing against Subscribe()/Consume() on a just-started broker was observed to
           occasionally time out. The group's first rebalance for a topic created out from under it can
           take longer than a test should have to wait, so pre-creating removes the race entirely. */
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();

        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = "items.lost_item.created", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "items.found_item.created", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "items.lost_item.updated", NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = "items.found_item.updated", NumPartitions = 1, ReplicationFactor = 1 }
        ]);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTestingKafka");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
                ["Kafka:GroupId"] = $"matching-service-tests-{Guid.NewGuid():N}",
                ["Kafka:TopicPrefix"] = "items",
                ["ConnectionStrings:MySql"] = _connectionString
            };

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IImageDescriptionRepository>();
            services.AddSingleton(Repository.Object);
        });
    }

    /* AddManualClaims (Program.cs) reads these before builder.Build(), too early for
       ConfigureAppConfiguration or UseEnvironment to reach (confirmed against MatchingServiceDbApiFactory:
       setting Jwt:* in the dictionary still throws). Env vars are the only channel that early.
       ItemService:BaseUrl must be HTTPS here too, since the env is still whatever the real process
       ASPNETCORE_ENVIRONMENT says, not yet "IntegrationTestingKafka". This test never mints or validates
       a real token, but the Jwt:* values must still match JwtTestTokenFactory's constants: see the
       matching comment in MatchingServiceDbApiFactory for why (a process-wide env var race with
       ClaimsApiFactory, which does validate real tokens, under xUnit's default parallel test classes). */
    private static void SetPreBuildEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtTestTokenFactory.Secret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtTestTokenFactory.Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtTestTokenFactory.Audience);
        Environment.SetEnvironmentVariable("ItemService__BaseUrl", "https://item-service.invalid");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _kafka.DisposeAsync();
        await _mysql.DisposeAsync();
    }
}
