using AdminVerifyService.Databases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MySql;

namespace AdminVerifyService.Tests.Support;

/// <summary>
/// Real, disposable MySQL running the app's real embedded migrations via
/// AdminVerifyService.Databases.DbInitializer - the same migration path the deployed service uses.
/// Plain repository/handler classes are constructed directly against a real IDbConnectionFactory;
/// Program.cs is intentionally NOT hosted here (the HTTP layer + CORS + JWT are exercised in the
/// API-integration step via WebApplicationFactory).
///
/// Requires Docker Desktop (or another Testcontainers-compatible Docker) running locally. DbInitializer
/// enforces the database name verbatim, so the container reuses the production name "admin_verify_db".
/// </summary>
public sealed class AdminVerifyDbFixture : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("admin_verify_db") // DbInitializer refuses any other database name.
        .WithUsername("admin_verify_test")
        .WithPassword("test_password")
        .Build();

    public IDbConnectionFactory Connections { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();

        var connectionString = new MySqlConnectionStringBuilder(
                _mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = connectionString
            })
            .Build();

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IDbConnectionFactory, DbConnectionFactory>()
            .BuildServiceProvider();

        DbInitializer.RunPendingMigrations(services, configuration);

        Connections = services.GetRequiredService<IDbConnectionFactory>();
    }

    public async Task DisposeAsync() => await _mysql.DisposeAsync();
}