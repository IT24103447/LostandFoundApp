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
    private MySqlContainer? _mysql;

    public FakeEventPublisher FakeEvents { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            _mysql = new MySqlBuilder()
                .WithImage("mysql:8")
                .WithDatabase("item_service")
                .WithUsername("root")
                .WithPassword("test_password")
                .Build();

            await _mysql.StartAsync();
        }
        catch
        {
            // Docker is not available in local environment; fallback to default config connection string
            _mysql = null;
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
                ["Item:MaxPhotosPerItem"] = "5",
                ["Item:MaxPhotoSizeBytes"] = "5242880",

                ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
            };

            if (_mysql != null)
            {
                var connectionString = new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
                {
                    AllowUserVariables = true
                }.ConnectionString;

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
