using AuthService.Services;
using Microsoft.AspNetCore.TestHost;
using MySqlConnector;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AuthService.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MySql;
using Xunit;

namespace AuthService.Tests.Integration;

/// <summary>
/// Boots the REAL AuthService app (real Program.cs, real routing, real auth middleware,
/// real SQL against a real database) for integration tests.
///
/// Only two things are swapped out, and only because they talk to the outside world:
///   - IEmailService  -> FakeEmailService   (no real SMTP/Mailtrap)
///   - IEventPublisher -> FakeEventPublisher (no real Kafka broker)
/// Everything else — UsersRepository, JwtTokenService, BCryptPasswordHasher, the
/// rate limiter, the auth middleware, the SQL migrations — runs for real.
///
/// Requires Docker Desktop (or another Testcontainers-compatible Docker) running locally.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("auth_service")
        .WithUsername("root")
        .WithPassword("test_password")
        .Build();

    public FakeEmailService FakeEmail { get; } = new();
    public FakeEventPublisher FakeEvents { get; } = new();

    public async Task InitializeAsync()
    {
        // Starts a throwaway MySQL container. First run pulls the mysql:8.0 image,
        // which can take a minute; subsequent runs reuse the cached image.
        await _mysql.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development"); // needed so Program.cs runs migrations + seeding on startup

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // Point the app at our disposable container instead of whatever
            // appsettings.Development.json has configured locally.
            var connectionString = new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
            {
                AllowUserVariables = true
            }.ConnectionString;

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = connectionString,
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Swap real SMTP for the in-memory fake.
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(FakeEmail);

            // Swap real Kafka publishing for the in-memory fake.
            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(FakeEvents);

            // The real KafkaEventProducerService is a background service that opens a
            // real Kafka producer on startup. We don't have a broker in tests, so remove it —
            // FakeEventPublisher above already captures what would have been published.
            services.RemoveAll<IHostedService>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _mysql.DisposeAsync();
    }
}

/// <summary>
/// Shares ONE MySQL container + app instance across every integration test class,
/// so we only pay the container-startup cost once per test run instead of once per class.
/// Tests must use unique emails/phone numbers per test (e.g. via Guid) to avoid colliding
/// with data left behind by other tests sharing this database.
/// </summary>

