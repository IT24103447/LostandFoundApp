using System.Collections.Generic;
using MatchingService.Claims;
using MatchingService.Tests.Support;
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
/// Real MySQL plus a real ASP.NET Core host with the real JWT bearer pipeline switched on. Item
/// Service is faked via ClaimItemClient's HttpMessageHandler; Kafka's hosted consumer is removed
/// since these tests never touch the image pipeline.
/// </summary>
public sealed class ClaimsApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8")
        .WithDatabase("matching_service")
        .WithUsername("matching_service_test")
        .WithPassword("test_password")
        .Build();

    public FakeItemServiceHandler ItemServiceHandler { get; } = new();

    /// <summary>
    /// Lets a test assert on rows the HTTP API never exposes (an appeal that must exist exactly once, a match
    /// that must not have been created). Same shape as MatchingServiceDbApiFactory's.
    /// </summary>
    public string GetConnectionString() =>
        new MySqlConnectionStringBuilder(_mysql.GetConnectionString())
        {
            SslMode = MySqlSslMode.None
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        SetPreBuildEnvironmentVariables();
        await _mysql.StartAsync();
        // Force the host to build and run its Development-only DbInitializer migration step now.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:MySql"] = GetConnectionString(),
                ["Kafka:BootstrapServers"] = "localhost:9092",
                ["Kafka:GroupId"] = "matching-service-claims-tests"

                // Jwt:*/ItemService:BaseUrl are set as env vars instead, see SetPreBuildEnvironmentVariables.
            };

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureTestServices(services =>
        {
            // No real broker in this test.
            services.RemoveAll<IHostedService>();

            /* Redirects ClaimItemClient's real HttpClient onto the fake Item Service, the standard way
               to fake a typed client's transport under WebApplicationFactory. The client's BaseAddress
               stays whatever ItemService:BaseUrl resolved to (never actually dialled): this handler
               intercepts every request regardless of target host. */
            services.AddHttpClient<ClaimItemClient>()
                .ConfigurePrimaryHttpMessageHandler(() => ItemServiceHandler);
        });
    }

    /* AddManualClaims (Program.cs) reads these before builder.Build(), too early for
       ConfigureAppConfiguration to reach (confirmed against MatchingServiceDbApiFactory: setting Jwt:*
       in the dictionary still throws). Env vars are the only channel that early. Jwt:Secret/Issuer/
       Audience must equal JwtTestTokenFactory's constants exactly, or tokens minted there fail real
       validation here (they are not faked: this factory runs the real JwtBearerHandler). */
    private static void SetPreBuildEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtTestTokenFactory.Secret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtTestTokenFactory.Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtTestTokenFactory.Audience);
        Environment.SetEnvironmentVariable("ItemService__BaseUrl", "https://item-service.invalid");
    }

    async Task IAsyncLifetime.DisposeAsync() => await _mysql.DisposeAsync();
}
