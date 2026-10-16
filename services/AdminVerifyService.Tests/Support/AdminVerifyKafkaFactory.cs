using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Testcontainers.Kafka;
using Testcontainers.MySql;

namespace AdminVerifyService.Tests.Support;

/// <summary>
/// Hosts the REAL Program.cs (WebApplicationFactory, Development environment so the Development-only
/// DbInitializer.RunPendingMigrations runs) against a real MySQL AND a real Kafka broker (both
/// Testcontainers, the same images production uses: mysql:8 and confluentinc/cp-kafka:7.5.0).
///
/// Unlike AdminVerifyApiFactory, the ListingEventConsumer hosted service is deliberately KEPT - it is
/// the subject under test and it consumes from the real broker. Notifications:Enabled=false (env var,
/// pre-build) keeps the SpamAlertWorker and its SMTP-requiring sender out of the host.
///
/// Configuration split matches AdminVerifyApiFactory (see its comment):
///  - pre-build reads (Jwt:*, Cors:AllowedOrigins, Notifications:Enabled) - environment variables;
///  - post-build reads (ConnectionStrings:MySql, Kafka:*) - ConfigureAppConfiguration.
///
/// Every topic is pre-created with ONE partition so per-topic FIFO ordering is strict; tests rely on
/// that ordering for quiescence (a trailing message's DB row existing implies every earlier message
/// on that topic was already consumed). Lazy auto-creation racing a just-started broker's first
/// Subscribe()/Consume() was observed to occasionally time out (MatchingService precedent).
///
/// The two *.resolved life-cycle topics are also pre-created so publishes to them are deterministic.
/// ListingEventConsumer intentionally does NOT subscribe to them - that is exactly the
/// "resolved-never-consumed" claim the KC-05 tests pin.
/// </summary>
public sealed class AdminVerifyKafkaFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string LostItemResolvedTopic = "items.lost_item.resolved";
    public const string FoundItemResolvedTopic = "items.found_item.resolved";

    private static readonly string[] AllTopics =
    [
        "items.lost_item.created",
        "items.found_item.created",
        "items.lost_item.updated",
        "items.found_item.updated",
        LostItemResolvedTopic,
        FoundItemResolvedTopic
    ];

    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.5.0") // the same image infra/kafka/docker-compose.yml uses.
        .Build();

    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("admin_verify_db") // DbInitializer refuses any other database name.
        .WithUsername("admin_verify_test")
        .WithPassword("test_password")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public string BootstrapServers => _kafka.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await Task.WhenAll(_kafka.StartAsync(), _mysql.StartAsync());

        ConnectionString = new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();

        await admin.CreateTopicsAsync(
            AllTopics.Select(name => new TopicSpecification
            {
                Name = name,
                NumPartitions = 1,
                ReplicationFactor = 1
            }));

        // Force the host to build and run its Development-only DbInitializer migration step now;
        // this also starts the real ListingEventConsumer against the real broker.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = ConnectionString,
                ["Kafka:BootstrapServers"] = BootstrapServers,
                ["Kafka:GroupId"] = "admin-verify-service-tests"

                // Jwt:*/Cors:AllowedOrigins/Notifications:Enabled are env vars, see SetPreBuildEnvironmentVariables.
            });
        });

        // No ConfigureTestServices on purpose: every real service (including the ListingEventConsumer
        // hosted service) stays registered - it is the subject under test.
    }

    /* Identical to AdminVerifyApiFactory.SetPreBuildEnvironmentVariables: Program.cs reads these
       before builder.Build(), so only environment variables reach them. Every factory in this
       project must use the same Jwt values (Environment.SetEnvironmentVariable is process-wide). */
    private static void SetPreBuildEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", AdminTestTokenFactory.Secret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", AdminTestTokenFactory.Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", AdminTestTokenFactory.Audience);
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "http://localhost:5173");
        Environment.SetEnvironmentVariable("Notifications__Enabled", "false");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        base.Dispose();
        await _kafka.DisposeAsync();
        await _mysql.DisposeAsync();
    }
}