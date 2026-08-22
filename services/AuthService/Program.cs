using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using AuthService.Configuration;
using AuthService.Databases;
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
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Bind strongly-typed configuration sections.
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<KafkaSettings>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<SendGridSettings>(builder.Configuration.GetSection("SendGrid"));

// Application services.
builder.Services.AddScoped<IUsersRepository, UsersRepository>();
builder.Services.AddScoped<IEmailVerificationTokensRepository, EmailVerificationTokensRepository>();
builder.Services.AddScoped<IEmailBouncesRepository, EmailBouncesRepository>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<PasswordValidator>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<IVerificationSessionService, VerificationSessionService>();
builder.Services.AddTransient<IEmailService, SmtpEmailService>();
builder.Services.AddTransient<IEmailBounceService, SendGridEmailBounceService>();

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
}

app.Run();
