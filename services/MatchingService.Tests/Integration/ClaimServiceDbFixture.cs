using MatchingService.Databases;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MySql;

namespace MatchingService.Tests.Integration;

/// <summary>
/// Real, disposable MySQL (Testcontainers) for Story 2's ClaimRepository, running the app's own real
/// migrations (001-005), the same DbInitializer path the real app uses. Unlike
/// MatchingServiceDbApiFactory, this does not host Program.cs at all: ClaimService/ClaimRepository/
/// ClaimItemClient are plain classes with no interfaces to swap via DI (unlike
/// IImageDescriptionRepository in Story 1), so tests construct them directly and only need a real
/// IDbConnectionFactory, not a full ASP.NET Core host, JWT bearer setup, or Kafka consumer.
/// The real JWT/HTTP host is exercised separately, in ClaimsApiFactory.
/// </summary>
public sealed class ClaimServiceDbFixture : IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("matching_service")
        .WithUsername("matching_service_test")
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

        // Same migration path the real app and MatchingServiceDbApiFactory both use.
        DbInitializer.RunPendingMigrations(services, configuration);

        Connections = services.GetRequiredService<IDbConnectionFactory>();
    }

    public async Task DisposeAsync() => await _mysql.DisposeAsync();
}
