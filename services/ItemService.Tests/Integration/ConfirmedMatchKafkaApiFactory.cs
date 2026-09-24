using System.Collections.Generic;
using System.Text;
using ItemService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using Testcontainers.Kafka;
using Testcontainers.MySql;
using Xunit;

/// <summary>
/// Story 4/5 shared (the Kafka-to-item-resolution consume half of the confirmation pipeline). Real,
/// disposable MySQL AND a real, disposable Kafka broker at once, deliberately - the rest of this
/// project avoids combining two real containers in one test (see MatchingServiceKafkaApiFactory's own
/// note), but ConfirmedMatchConsumer/ConfirmedMatchHandler's whole job is turning a real Kafka message
/// into a real database transaction, so there is no way to prove that wiring genuinely works without
/// both being real. Unlike ItemServiceApiFactory, IHostedService is NOT removed here: the real
/// ConfirmedMatchConsumer (and OutboxRelayService alongside it, harmlessly, since nothing exercises
/// the item-creation outbox in these tests) actually run.
/// </summary>
public sealed class ConfirmedMatchKafkaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("item_service")
        .WithUsername("item_service_test")
        .WithPassword("test_password")
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    public string GetKafkaBootstrapAddress() => _kafka.GetBootstrapAddress();

    public string GetTestDatabaseConnectionStringForAssertions() => GetTestDatabaseConnectionString();

    private string GetTestDatabaseConnectionString() =>
        new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            AllowUserVariables = true,
            SslMode = MySqlSslMode.None
        }.ConnectionString;

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
        await _mysql.StartAsync();
        await WaitForMySqlReadyAsync();
        await _kafka.StartAsync();

        // The "matches.confirmed" topic is left to the broker's own default auto-creation (on first
        // produce/consume) rather than pre-created via AdminClient - ConfirmedMatchConsumerKafkaIntegrationTests'
        // own 30-second polling window absorbs the same first-topic-access race
        // MatchingServiceKafkaApiFactory's comment describes, without needing an admin client here.

        // Force the host (and its real hosted services, including ConfirmedMatchConsumer) to actually start.
        _ = Server;
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

                ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
                ["Kafka:TopicPrefix"] = "items",

                ["Item:PhotoStoragePath"] = "wwwroot/photos-test-kafka",
                ["Item:PhotoPublicPath"] = "/photos",
                ["Item:MaxPhotosPerItem"] = "1",
                ["Item:MaxPhotoSizeBytes"] = "5242880",

                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",

                ["ConnectionStrings:MySql"] = GetTestDatabaseConnectionString()
            };

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureTestServices(services =>
        {
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
        await _mysql.DisposeAsync();
        await _kafka.DisposeAsync();
    }
}
