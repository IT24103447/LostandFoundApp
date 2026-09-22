using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Real, disposable MySQL (Testcontainers) running the app's own real migrations. This proves
/// ImageDescriptionRepository's actual SQL, the unique-constraint idempotency, and the
/// claim/lease/complete/fail lifecycle work against a real database, not just that the repository
/// interface is called correctly (see the Kafka factory for that, kept separate on purpose. One real
/// dependency per test matches ItemServiceApiFactory's own pattern).
///
/// DbInitializer requires the database to be named exactly "matching_service", so the container is
/// configured to match. Kafka's hosted consumer is removed here since this test has no real broker.
/// </summary>
public sealed class MatchingServiceDbApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("matching_service")
        .WithUsername("matching_service_test")
        .WithPassword("test_password")
        .Build();

    public string GetConnectionString() =>
        new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await _mysql.StartAsync();
        /* Force the host to build and run its Development-only DbInitializer migration step now,
           rather than lazily on first use inside a test. */
        _ = Server;
    }

    /* AddManualClaims (Program.cs) reads these before builder.Build(), too early for
       ConfigureAppConfiguration to reach (confirmed: setting them in the dictionary below still throws).
       Env vars are the only channel that early. Values are never dereferenced, just format-checked. */
    private static void SetPreBuildEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", "matching-service-db-tests-secret-key-32-bytes-minimum");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "matching-service-db-tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "matching-service-db-tests");
        Environment.SetEnvironmentVariable("ItemService__BaseUrl", "https://item-service.invalid");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        /* "Development" specifically: DbInitializer.RunPendingMigrations only runs in this
           environment, the same gate the real app uses in Program.cs. It never auto-runs in Production. */
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = GetConnectionString(),

                /* Required for KafkaSettings' ValidateOnStart(). This is never actually dialled since
                   the consumer's hosted service is removed below. */
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:GroupId"] = "matching-service-db-tests"

                // Jwt:*/ItemService:BaseUrl are set as env vars instead, see SetPreBuildEnvironmentVariables.
            };

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureTestServices(services =>
        {
            // No real broker in this test. The consumer must not attempt to connect.
            services.RemoveAll<IHostedService>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync() => await _mysql.DisposeAsync();
}
