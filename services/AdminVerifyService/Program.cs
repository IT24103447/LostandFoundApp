using AdminVerifyService.Configuration;
using AdminVerifyService.Databases;
using AdminVerifyService.Listings;
using AdminVerifyService.Spam;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAdminSecurity(builder.Configuration);

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

builder.Services.AddOptions<DetectionSettings>()
    .Bind(builder.Configuration.GetSection("Detection"))
    .Validate(
        settings =>
            settings.SpamThreshold >= 2 &&
            settings.SpamWindowMinutes >= 1 &&
            settings.SpamRecordCap >= 0,
        "Detection settings are invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<TrackedListingRepository>();
builder.Services.AddSingleton<SpamRecordRepository>();
builder.Services.AddSingleton<SpamRule>();
builder.Services.AddSingleton<SpamReviewRepository>();
builder.Services.AddSingleton<SpamCaseRepository>();
builder.Services.AddSingleton<SpamSolveRepository>();
builder.Services.AddSingleton<ListingEventHandler>();
builder.Services.AddHostedService<ListingEventConsumer>();

if (builder.Configuration.GetValue<bool>("Notifications:Enabled"))
{
    builder.Services.AddSingleton<SpamAlertRepository>();
    builder.Services.AddSingleton<ISpamAlertEmailSender, SpamAlertEmailSender>();
    builder.Services.AddHostedService<SpamAlertWorker>();
}

var app = builder.Build();

app.UseExceptionHandler(errorApplication =>
{
    errorApplication.Run(async context =>
    {
        var logger = context.RequestServices
            .GetRequiredService<ILogger<Program>>();

        logger.LogError("ADMIN_VERIFY_API_UNHANDLED_ERROR");

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

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

    DbInitializer.RunPendingMigrations(app.Services, app.Configuration);
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors(SecurityRegistration.CorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program
{
    protected Program() { }
}
