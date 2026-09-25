using System.Security.Claims;
using System.Text;
using Confluent.Kafka;
using ItemService.Configuration;
using ItemService.Databases;
using ItemService.Filters;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<RequestTransactionFilter>())
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddTransient<IDbConnectionFactory, DbConnectionFactory>();
// One per request: repository writes and the outbox event insert share its transaction.
builder.Services.AddScoped<IDbSession, DbSession>();

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
builder.Services.Configure<OutboxSettings>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<BlobStorageSettings>(builder.Configuration.GetSection("BlobStorage"));

builder.Services.AddScoped<ILostItemsRepository, LostItemsRepository>();
builder.Services.AddScoped<IFoundItemsRepository, FoundItemsRepository>();
builder.Services.AddScoped<IItemsSearchRepository, ItemsSearchRepository>();

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

// Transactional outbox: controllers call IEventPublisher, which now records the event in the outbox_events
// table inside the request's DB transaction. OutboxRelayService delivers it to Kafka in the background.
builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher>();
builder.Services.AddSingleton<IOutboxStore, OutboxStore>();
builder.Services.AddHostedService<OutboxRelayService>();

// changed during sprint 3 by dev
builder.Services.AddScoped<ConfirmedMatchHandler>();
builder.Services.AddHostedService<ConfirmedMatchConsumer>();

// Producer used only by the relay. Durability now lives in the database, so a failed send can be short:
// the relay retries with backoff instead of keeping messages queued in memory for minutes.
builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<KafkaSettings>>().Value;
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Kafka.Producer");

    var config = new ProducerConfig
    {
        BootstrapServers = settings.BootstrapServers,
        EnableIdempotence = false,
        Acks = Acks.All,
        MessageSendMaxRetries = 3,
        RetryBackoffMs = 500,
        MessageTimeoutMs = 30_000 // each ProduceAsync fails within 30 s if the broker is unreachable
    };

    return new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) =>
            logger.LogWarning("Kafka producer error: {Reason}", e.Reason))
        .SetLogHandler((_, log) =>
            logger.LogDebug("Kafka: {Message}", log.Message))
        .Build();
});

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
