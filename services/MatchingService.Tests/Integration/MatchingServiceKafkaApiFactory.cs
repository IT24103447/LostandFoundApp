using System.Collections.Generic;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Testcontainers.Kafka;
using MatchingService.Tests.Support;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Real, disposable Kafka broker (Testcontainers) wired to the real ItemCreatedEventConsumer and
/// ImageDescriptionEventHandler. IImageDescriptionRepository is mocked here on purpose. This factory
/// proves the Kafka wiring itself (subscribe, consume, deserialize, dispatch, commit) works against a
/// real broker, not the SQL layer. See MatchingServiceDbApiFactory for that, kept as a separate real
/// dependency rather than combining two real containers in one test, matching ItemServiceApiFactory's
/// own pattern of real MySQL, faked Kafka, never both real at once.
/// ImageProcessing:Enabled stays false (the default), so the Gemini client and ImageDescriptionWorker
/// are never registered and no Gemini/Blob config is needed for this test.
/// </summary>
public sealed class MatchingServiceKafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    public Mock<IImageDescriptionRepository> Repository { get; } = new();

    public string GetBootstrapAddress() => _kafka.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await _kafka.StartAsync();

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
        /* Not "Development": DbInitializer must not try to run migrations against a real database.
           This test only needs the Kafka pipeline, not MySQL. */
        builder.UseEnvironment("IntegrationTestingKafka");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
                ["Kafka:GroupId"] = $"matching-service-tests-{Guid.NewGuid():N}",
                ["Kafka:TopicPrefix"] = "items",

                /* Never dereferenced: the repository is fully replaced below, and
                   ImageProcessing:Enabled stays false so nothing else touches this connection string. */
                ["ConnectionStrings:MySql"] = "Server=unused;Database=unused;Uid=unused;Pwd=unused;"
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

    async Task IAsyncLifetime.DisposeAsync() => await _kafka.DisposeAsync();
}
