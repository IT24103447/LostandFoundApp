using Confluent.Kafka;
using Google.GenAI;
using MatchingService.Configuration;
using MatchingService.Databases;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;

        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHealthChecks();

builder.Services.AddOptions<KafkaSettings>()
    .Bind(builder.Configuration.GetSection("Kafka"))
    .Validate(
        settings => !string.IsNullOrWhiteSpace(settings.BootstrapServers),
        "Kafka:BootstrapServers is required.")
    .Validate(
        settings => !string.IsNullOrWhiteSpace(settings.GroupId),
        "Kafka:GroupId is required.")
    .ValidateOnStart();

builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection("Gemini"));

builder.Services.Configure<BlobStorageSettings>(
    builder.Configuration.GetSection("BlobStorage"));

builder.Services.Configure<ImageProcessingSettings>(
    builder.Configuration.GetSection("ImageProcessing"));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<
    IImageDescriptionRepository,
    ImageDescriptionRepository>();

builder.Services.AddSingleton<BlobUrlPhotoKeyGenerator>();
builder.Services.AddSingleton<ImageDescriptionEventHandler>();

builder.Services.AddSingleton<IConsumer<string, string>>(services =>
{
    var settings = services
        .GetRequiredService<IOptions<KafkaSettings>>()
        .Value;

    var logger = services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Kafka.Consumer");

    var config = new ConsumerConfig
    {
        BootstrapServers = settings.BootstrapServers,
        GroupId = settings.GroupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false
    };

    return new ConsumerBuilder<string, string>(config)
        .SetErrorHandler((_, _) =>
            logger.LogWarning("Kafka reported a consumer error."))
        .Build();
});

builder.Services.AddHostedService<ItemCreatedEventConsumer>();

var processingEnabled = builder.Configuration
    .GetValue<bool>("ImageProcessing:Enabled");

if (processingEnabled)
{
    builder.Services.AddOptions<GeminiSettings>()
        .Bind(builder.Configuration.GetSection("Gemini"))
        .Validate(
            settings => !string.IsNullOrWhiteSpace(settings.ApiKey),
            "Gemini:ApiKey is required when processing is enabled.")
        .Validate(
            settings =>
                !string.IsNullOrWhiteSpace(settings.Model) &&
                settings.Model.Length <= 150,
            "Gemini:Model is required and must not exceed 150 characters.")
        .ValidateOnStart();

    builder.Services.AddOptions<BlobStorageSettings>()
        .Bind(builder.Configuration.GetSection("BlobStorage"))
        .Validate(
            settings =>
                !string.IsNullOrWhiteSpace(settings.AllowedHost) &&
                Uri.CheckHostName(settings.AllowedHost.Trim()) ==
                UriHostNameType.Dns,
            "BlobStorage:AllowedHost must contain a hostname without a URL.")
        .ValidateOnStart();

    builder.Services.AddOptions<ImageProcessingSettings>()
        .Bind(builder.Configuration.GetSection("ImageProcessing"))
        .Validate(
            settings =>
                settings.PollIntervalSeconds >= 1 &&
                settings.RequestTimeoutSeconds >= 10 &&
                settings.LeaseSeconds >=
                    settings.RequestTimeoutSeconds + 60 &&
                settings.MaxAttempts is >= 1 and <= 10 &&
                settings.InitialRetrySeconds >= 1 &&
                settings.MaxRetrySeconds >= settings.InitialRetrySeconds,
            "ImageProcessing settings are invalid.")
        .ValidateOnStart();

    builder.Services.AddSingleton<Client>(services =>
    {
        var settings = services
            .GetRequiredService<IOptions<GeminiSettings>>()
            .Value;

        return new Client(apiKey: settings.ApiKey);
    });

    builder.Services.AddSingleton<BlobUrlValidator>();
    builder.Services.AddSingleton<ImageDescriptionValidator>();

    builder.Services.AddSingleton<
        IImageDescriptionGenerator,
        GeminiImageDescriptionGenerator>();

    builder.Services.AddHostedService<ImageDescriptionWorker>();
}

var app = builder.Build();

app.UseExceptionHandler(errorApplication =>
{
    errorApplication.Run(async context =>
    {
        var logger = context.RequestServices
            .GetRequiredService<ILogger<Program>>();

        logger.LogError("MATCHING_API_UNHANDLED_ERROR");

        context.Response.StatusCode =
            StatusCodes.Status500InternalServerError;

        await context.Response.WriteAsJsonAsync(new
        {
            error = "An internal error occurred."
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    DbInitializer.RunPendingMigrations(
        app.Services,
        app.Configuration);
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();

// Liveness only; no descriptions, URLs, credentials or job status.
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }