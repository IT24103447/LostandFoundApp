using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MatchingService.Claims;

public static class ClaimRegistration
{
    public static IServiceCollection AddManualClaims(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var secret = configuration["Jwt:Secret"];
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];

        if (string.IsNullOrWhiteSpace(secret) ||
            Encoding.UTF8.GetByteCount(secret) < 32 ||
            string.IsNullOrWhiteSpace(issuer) ||
            string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "Matching Service requires Jwt:Secret, " +
                "Jwt:Issuer and Jwt:Audience.");
        }

        var itemServiceUrl =
            configuration["ItemService:BaseUrl"];

        if (!Uri.TryCreate(
                itemServiceUrl,
                UriKind.Absolute,
                out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp &&
             baseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException(
                "ItemService:BaseUrl must be a valid HTTP or HTTPS URL.");
        }

        if (!environment.IsDevelopment() &&
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "ItemService:BaseUrl must use HTTPS outside Development.");
        }

        var origins = configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>()
            ?? ["http://localhost:5173"];

        services.AddCors(options =>
        {
            options.AddPolicy(
                "matching-frontend",
                policy =>
                {
                    policy
                        .WithOrigins(origins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
        });

        services
            .AddAuthentication(
                JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters =
                    new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = issuer,
                        ValidAudience = audience,
                        IssuerSigningKey =
                            new SymmetricSecurityKey(
                                Encoding.UTF8.GetBytes(secret)),
                        ClockSkew = TimeSpan.FromMinutes(2),
                        ValidAlgorithms =
                        [
                            SecurityAlgorithms.HmacSha256
                        ]
                    };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authorization = context.Request
                            .Headers.Authorization
                            .ToString();

                        if (string.IsNullOrWhiteSpace(
                                authorization) &&
                            context.Request.Cookies.TryGetValue(
                                "auth_token",
                                out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                "VerifiedClaimUser",
                policy =>
                {
                    policy
                        .RequireAuthenticatedUser()
                        .RequireClaim("email_verified", "1");
                });
        });

        services.AddHttpContextAccessor();

        services.AddHttpClient<ClaimItemClient>(client =>
        {
            client.BaseAddress = new Uri(
                baseUri.AbsoluteUri.TrimEnd('/') + "/");

            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false
            });

        services.AddScoped<ClaimRepository>();
        services.AddScoped<ClaimService>();

        return services;
    }
}