using System.Collections.Generic;
using System.Text;
using ItemService.Services;
using ItemService.Tests.Integration.Fakes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

public class ItemServiceApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Shared xUnit infrastructure: starts a disposable MySQL-backed API and replaces real Kafka with a recording publisher.
    private MySqlContainer? _mysql;

    public FakeEventPublisher FakeEvents { get; } = new();

    public string GetTestDatabaseConnectionString()
    {
        if (_mysql is null)
            throw new InvalidOperationException("The MySQL Testcontainers database is unavailable. Start Docker Desktop before running integration tests.");

        return new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            AllowUserVariables = true,
            SslMode = MySqlSslMode.None
        }.ConnectionString;
    }

    private async Task WaitForMySqlReadyAsync()
    {
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using var connection = new MySqlConnection(GetTestDatabaseConnectionString());
                await connection.OpenAsync();
                return;
            }
            catch (MySqlException ex)
            {
                lastFailure = ex;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new InvalidOperationException("The disposable MySQL container did not become ready within 30 seconds.", lastFailure);
    }

    public async Task InitializeAsync()
    {
        try
        {
            _mysql = new MySqlBuilder()
                .WithImage("mysql:8")
                .WithDatabase("item_service")
                .WithUsername("item_service_test")
                .WithPassword("test_password")
                .Build();

            await _mysql.StartAsync();
            await WaitForMySqlReadyAsync();
        }
        catch (Exception ex)
        {
            // Never fall back to the developer's configured database. Integration tests create
            // temporary reports and must run only against their disposable Testcontainers MySQL.
            throw new InvalidOperationException(
                "Docker/Testcontainers MySQL could not start. Start Docker Desktop and run 'docker info' before executing HTTP/MySQL integration tests.",
                ex);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = TestAuthHelper.TestJwtSecret,
                ["Jwt:Issuer"] = TestAuthHelper.TestJwtIssuer,
                ["Jwt:Audience"] = TestAuthHelper.TestJwtAudience,
                ["Jwt:ExpiryMinutes"] = "1440",

                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:TopicPrefix"] = "items",

                ["BlobStorage:ConnectionString"] = "",
                ["Item:PhotoStoragePath"] = "wwwroot/photos-test",
                ["Item:PhotoPublicPath"] = "/photos",
                ["Item:MaxPhotosPerItem"] = "1",
                ["Item:MaxPhotoSizeBytes"] = "5242880",

                ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
            };

            if (_mysql != null)
            {
                // The disposable local MySQL container does not need TLS. Disabling it
                // keeps the integration suite independent of host SSPI credentials.
                var connectionString = GetTestDatabaseConnectionString();

                dict["ConnectionStrings:MySql"] = connectionString;
            }

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(FakeEvents);

            services.RemoveAll<IHostedService>();

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.ValidIssuer = TestAuthHelper.TestJwtIssuer;
                options.TokenValidationParameters.ValidAudience = TestAuthHelper.TestJwtAudience;
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestAuthHelper.TestJwtSecret));
            });
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_mysql != null)
        {
            await _mysql.DisposeAsync();
        }
    }
}
