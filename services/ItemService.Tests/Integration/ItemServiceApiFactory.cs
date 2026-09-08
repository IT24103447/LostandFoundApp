using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public class ItemServiceApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
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
            });
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
}
