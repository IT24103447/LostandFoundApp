using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using AuthService.Configuration;
using AuthService.Databases;
using AuthService.Models;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddTransient<IDbConnectionFactory, DbConnectionFactory>();

// CORS for the Vite dev server (default port 5173). Tightened per environment before prod.
const string DevCorsPolicy = "dev-cors";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Bind strongly-typed configuration sections.
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<KafkaSettings>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));

// Application services.
builder.Services.AddScoped<IUsersRepository, UsersRepository>();
builder.Services.AddScoped<IEmailVerificationTokensRepository, EmailVerificationTokensRepository>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<PasswordValidator>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IVerificationSessionService, VerificationSessionService>();
builder.Services.AddTransient<IEmailService, SmtpEmailService>();

// JWT bearer authentication.
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException("Jwt settings not configured.");

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = "is_admin"
        };

        // Read JWT ONLY from the httpOnly cookie — no Authorization: Bearer fallback.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("auth_token", out var token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Rate limiting: fixed window on unauthenticated endpoints.
builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("register", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
    });
    o.AddFixedWindowLimiter("verify-email", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(1);
    });
    o.AddFixedWindowLimiter("resend-verification", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromHours(1);
    });
    o.AddFixedWindowLimiter("login", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromMinutes(5);
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(DevCorsPolicy);

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Run pending SQL migrations in Development only — never auto-DDL in production.
if (app.Environment.IsDevelopment())
{
    DbInitializer.RunPendingMigrations(app.Services, app.Configuration);

    using (var scope = app.Services.CreateScope())
    {
        var usersRepo = scope.ServiceProvider.GetRequiredService<IUsersRepository>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        await SeedUsersAsync(usersRepo, passwordHasher);
    }
}

app.Run();

static async Task SeedUsersAsync(IUsersRepository repo, IPasswordHasher hasher)
{
    var existing = await repo.GetByEmailAsync("admin1@lostandfound.com");
    if (existing is not null) return;

    var seedUsers = new[]
    {
        new { Email = "admin1@lostandfound.com", Password = "Admin123!", Name = "Admin One", Phone = "+94770000001", IsAdmin = true },
        new { Email = "admin2@lostandfound.com", Password = "Admin123!", Name = "Admin Two", Phone = "+94770000002", IsAdmin = true },
        new { Email = "admin3@lostandfound.com", Password = "Admin123!", Name = "Admin Three", Phone = "+94770000003", IsAdmin = true },
        new { Email = "user1@example.com", Password = "User123!", Name = "User One", Phone = "+94770000011", IsAdmin = false },
        new { Email = "user2@example.com", Password = "User123!", Name = "User Two", Phone = "+94770000012", IsAdmin = false },
        new { Email = "user3@example.com", Password = "User123!", Name = "User Three", Phone = "+94770000013", IsAdmin = false },
        new { Email = "user4@example.com", Password = "User123!", Name = "User Four", Phone = "+94770000014", IsAdmin = false },
        new { Email = "user5@example.com", Password = "User123!", Name = "User Five", Phone = "+94770000015", IsAdmin = false },
        new { Email = "user6@example.com", Password = "User123!", Name = "User Six", Phone = "+94770000016", IsAdmin = false },
        new { Email = "user7@example.com", Password = "User123!", Name = "User Seven", Phone = "+94770000017", IsAdmin = false },
        new { Email = "user8@example.com", Password = "User123!", Name = "User Eight", Phone = "+94770000018", IsAdmin = false },
        new { Email = "user9@example.com", Password = "User123!", Name = "User Nine", Phone = "+94770000019", IsAdmin = false },
        new { Email = "user10@example.com", Password = "User123!", Name = "User Ten", Phone = "+94770000020", IsAdmin = false },
        new { Email = "user11@example.com", Password = "User123!", Name = "User Eleven", Phone = "+94770000021", IsAdmin = false },
        new { Email = "user12@example.com", Password = "User123!", Name = "User Twelve", Phone = "+94770000022", IsAdmin = false },
        new { Email = "user13@example.com", Password = "User123!", Name = "User Thirteen", Phone = "+94770000023", IsAdmin = false },
        new { Email = "user14@example.com", Password = "User123!", Name = "User Fourteen", Phone = "+94770000024", IsAdmin = false },
        new { Email = "user15@example.com", Password = "User123!", Name = "User Fifteen", Phone = "+94770000025", IsAdmin = false },
        new { Email = "user16@example.com", Password = "User123!", Name = "User Sixteen", Phone = "+94770000026", IsAdmin = false },
        new { Email = "user17@example.com", Password = "User123!", Name = "User Seventeen", Phone = "+94770000027", IsAdmin = false },
        new { Email = "user18@example.com", Password = "User123!", Name = "User Eighteen", Phone = "+94770000028", IsAdmin = false },
        new { Email = "user19@example.com", Password = "User123!", Name = "User Nineteen", Phone = "+94770000029", IsAdmin = false },
        new { Email = "user20@example.com", Password = "User123!", Name = "User Twenty", Phone = "+94770000030", IsAdmin = false }
    };

    foreach (var u in seedUsers)
    {
        var hash = hasher.Hash(u.Password);
        await repo.CreateAsync(new User
        {
            Id = Guid.NewGuid(),
            Email = u.Email,
            PasswordHash = hash,
            Name = u.Name,
            PhoneNo = u.Phone,
            IsAdmin = u.IsAdmin,
            IsEmailVerified = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }
}
