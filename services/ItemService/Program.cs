using System.Security.Claims;
using System.Text;
using ItemService.Configuration;
using ItemService.Databases;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddTransient<IDbConnectionFactory, DbConnectionFactory>();

const string DevCorsPolicy = "dev-cors";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddPolicy(DevCorsPolicy, p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<ItemSettings>(builder.Configuration.GetSection("Item"));
builder.Services.Configure<KafkaSettings>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<BlobStorageSettings>(builder.Configuration.GetSection("BlobStorage"));

builder.Services.AddScoped<ILostItemsRepository, LostItemsRepository>();
builder.Services.AddScoped<IFoundItemsRepository, FoundItemsRepository>();
// QA/CI temporary workaround: these search-repository types are not included in this checkout.
// Restore this registration when IItemsSearchRepository and ItemsSearchRepository are restored.
// builder.Services.AddScoped<IItemsSearchRepository, ItemsSearchRepository>();

var blobConnectionString = builder.Configuration["BlobStorage:ConnectionString"];

if (string.IsNullOrWhiteSpace(blobConnectionString) && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "BlobStorage:ConnectionString is required in Production. " +
        "Refusing to fall back to local disk storage, which does not persist across " +
        "restarts/redeploys and is not shared across instances.");
}

if (!string.IsNullOrWhiteSpace(blobConnectionString))
{
    builder.Services.AddSingleton<IPhotoStorageService, AzureBlobPhotoStorageService>();
}
else
{
    builder.Services.AddScoped<IPhotoStorageService, LocalPhotoStorageService>();
}

builder.Services.AddSingleton<KafkaEventPublisher>();
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<KafkaEventPublisher>());
builder.Services.AddHostedService<KafkaEventProducerService>();

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors(DevCorsPolicy);

app.UseExceptionHandler(appBuilder =>
{
    appBuilder.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError("Unhandled exception occurred");

        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"An internal error occurred.\"}");
    });
});

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();

app.MapControllers();

if (app.Environment.IsDevelopment())
{
    DbInitializer.RunPendingMigrations(app.Services, app.Configuration);
}

app.Run();

public partial class Program { }
