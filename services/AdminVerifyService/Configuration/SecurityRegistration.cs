using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AdminVerifyService.Configuration;

public static class SecurityRegistration
{
    public const string CorsPolicy = "admin-frontend";
    public const string AdminOnlyPolicy = "AdminOnly";

    private const string AuthCookieName = "auth_token";

    public static IServiceCollection AddAdminSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
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
                "Admin Verify Service requires Jwt:Secret, Jwt:Issuer and Jwt:Audience.");
        }

        var origins = configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? [];

        services.AddCors(options =>
            options.AddPolicy(CorsPolicy, policy =>
                policy.WithOrigins(origins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    ClockSkew = TimeSpan.FromMinutes(2),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.ToString()) &&
                            context.Request.Cookies.TryGetValue(AuthCookieName, out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
            options.AddPolicy(AdminOnlyPolicy, policy =>
                policy.RequireAuthenticatedUser()
                    .RequireClaim("is_admin", "1")));

        return services;
    }
}
