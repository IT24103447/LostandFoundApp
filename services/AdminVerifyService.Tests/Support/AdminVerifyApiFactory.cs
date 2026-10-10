using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Testcontainers.MySql;

namespace AdminVerifyService.Tests.Support;

/// <summary>
/// Hosts the real Program.cs (WebApplicationFactory) against a real, disposable MySQL running the
/// app's real embedded migrations, in the Development environment so the Development-only
/// DbInitializer.RunPendingMigrations step executes exactly as it does for the deployed service.
/// Requests are authenticated with real HS256 JWTs minted by AdminTestTokenFactory, so the full
/// JwtBearer + AdminOnly policy path is exercised over HTTP - no test auth handler substitutes for it.
///
/// Configuration split (the LF-338 / MatchingService precedent, proven empirically there):
/// - Settings Program.cs reads BEFORE builder.Build() - Jwt:* and Cors:AllowedOrigins inside
///   AddAdminSecurity, plus the Notifications:Enabled hosted-service gate - cannot come from
///   ConfigureAppConfiguration; they are applied as environment variables, the only channel that early.
/// - Settings read after build (ConnectionStrings:MySql, Kafka:*) are set via ConfigureAppConfiguration.
///
/// Kafka's hosted consumer is removed because this factory has no real broker; the alert worker is
/// never registered because Notifications:Enabled=false.
/// </summary>
public sealed class AdminVerifyApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("admin_verify_db") // DbInitializer refuses any other database name.
        .WithUsername("admin_verify_test")
        .WithPassword("test_password")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await _mysql.StartAsync();

        ConnectionString = new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

        // Force the host to build and run its Development-only DbInitializer migration step now,
        // rather than lazily on first use inside a test.
        _ = Server;
    }

    /* Program.cs's AddAdminSecurity reads Jwt:* and Cors:AllowedOrigins before builder.Build() -
       too early for ConfigureAppConfiguration to reach (the MatchingService precedent confirmed this
       empirically). Notifications:Enabled gates the alert worker's registration in Program.cs, also
       pre-build. Every factory in this project must use the exact same Jwt values, because
       Environment.SetEnvironmentVariable is process-wide. */
    private static void SetPreBuildEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", AdminTestTokenFactory.Secret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", AdminTestTokenFactory.Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", AdminTestTokenFactory.Audience);
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "http://localhost:5173");
        Environment.SetEnvironmentVariable("Notifications__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        /* "Development" specifically: DbInitializer.RunPendingMigrations only runs in this
           environment, the same gate the real app uses in Program.cs. It never auto-runs in Production. */
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = ConnectionString,

                /* Required for KafkaSettings' ValidateOnStart(). Never actually dialled since the
                   consumer's hosted service is removed below. */
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:GroupId"] = "admin-verify-api-tests"

                // Jwt:*/Cors:AllowedOrigins/Notifications:Enabled are env vars, see SetPreBuildEnvironmentVariables.
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // No real broker in this test. The consumer must not attempt to connect.
            services.RemoveAll<IHostedService>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        base.Dispose();
        await _mysql.DisposeAsync();
    }
}