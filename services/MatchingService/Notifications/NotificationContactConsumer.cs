using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using MatchingService.Databases;
using MySqlConnector;

namespace MatchingService.Notifications;

public sealed class NotificationContactConsumer(
    IDbConnectionFactory connections,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<NotificationContactConsumer> logger) : BackgroundService
{
    private static readonly string[] Topics =
    [
        "auth.user.verified",
        "auth.user.profile_updated",
        "auth.user.kicked",
        "auth.user.unkicked",
        "auth.user.deleted"
    ];

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        var bootstrapServers =
            configuration["Kafka:BootstrapServers"]
            ?? "localhost:9092";

        if (environment.IsDevelopment())
        {
            await EnsureDevelopmentTopicsAsync(
                bootstrapServers,
                stoppingToken);
        }

        using var consumer =
            new ConsumerBuilder<string, string>(
                new ConsumerConfig
                {
                    BootstrapServers = bootstrapServers,
                    GroupId =
                        "matching-service-notification-contacts",
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    EnableAutoCommit = false,
                    EnableAutoOffsetStore = false
                }).Build();

        consumer.Subscribe(Topics);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;

                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    logger.LogWarning(
                        "Auth event consumption will retry. Kafka code: {Code}.",
                        exception.Error.Code);

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);
                    continue;
                }

                try
                {
                    var contact = Parse(
                        result.Topic,
                        result.Message?.Value);

                    if (contact is not null)
                    {
                        await SaveAsync(
                            contact,
                            stoppingToken);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Invalid Auth contact event discarded at {Position}.",
                            result.TopicPartitionOffset);
                    }

                    consumer.Commit(result);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        "Auth contact event will retry at {Position}. Type: {Type}.",
                        result.TopicPartitionOffset,
                        exception.GetType().Name);

                    try
                    {
                        consumer.Seek(
                            result.TopicPartitionOffset);
                    }
                    catch (KafkaException)
                    {
                        consumer.Unsubscribe();
                        consumer.Subscribe(Topics);
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task EnsureDevelopmentTopicsAsync(
        string bootstrapServers,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var admin =
                    new AdminClientBuilder(
                        new AdminClientConfig
                        {
                            BootstrapServers =
                                bootstrapServers,
                            SocketTimeoutMs = 10_000
                        }).Build();

                await admin.CreateTopicsAsync(
                    Topics.Select(topic =>
                        new TopicSpecification
                        {
                            Name = topic,
                            NumPartitions = 1,
                            ReplicationFactor = 1
                        }),
                    new CreateTopicsOptions
                    {
                        RequestTimeout =
                            TimeSpan.FromSeconds(10)
                    });

                return;
            }
            catch (CreateTopicsException exception)
                when (exception.Results.All(result =>
                    result.Error.Code is
                        ErrorCode.NoError or
                        ErrorCode.TopicAlreadyExists))
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Auth topic setup will retry. Type: {Type}.",
                    exception.GetType().Name);

                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
        }
    }

    private static ContactEvent? Parse(
        string topic,
        string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) ||
            !Topics.Contains(topic, StringComparer.Ordinal))
        {
            return null;
        }

        try
        {
            using var document =
                JsonDocument.Parse(payload);

            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "userId",
                    out var userIdValue) ||
                !userIdValue.TryGetGuid(out var userId) ||
                userId == Guid.Empty ||
                !root.TryGetProperty(
                    "email",
                    out var emailValue) ||
                !root.TryGetProperty(
                    "timestamp",
                    out var timestampValue) ||
                !timestampValue.TryGetDateTime(
                    out var timestamp))
            {
                return null;
            }

            var email = emailValue.GetString();

            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            return new ContactEvent(
                userId,
                email,
                topic != "auth.user.kicked" &&
                    topic != "auth.user.deleted",
                topic == "auth.user.deleted",
                timestamp);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SaveAsync(
        ContactEvent contact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO notification_contacts (
                user_id,
                email,
                is_active,
                is_deleted,
                last_event_at,
                updated_at
            )
            VALUES (
                @userId,
                @email,
                @active,
                @deleted,
                @eventAt,
                @now
            )
            ON DUPLICATE KEY UPDATE
                email =
                    CASE
                        WHEN @eventAt >= last_event_at
                             AND is_deleted = 0
                        THEN @email
                        ELSE email
                    END,
                is_active =
                    CASE
                        WHEN is_deleted = 1 THEN 0
                        WHEN @eventAt >= last_event_at
                        THEN @active
                        ELSE is_active
                    END,
                is_deleted =
                    CASE
                        WHEN @deleted = 1
                             AND @eventAt >= last_event_at
                        THEN 1
                        ELSE is_deleted
                    END,
                last_event_at =
                    GREATEST(last_event_at, @eventAt),
                updated_at = @now;
            """;

        await using var connection =
            connections.Create();

        await connection.OpenAsync(
            cancellationToken);

        await using var command =
            new MySqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "@userId",
            contact.UserId);
        command.Parameters.AddWithValue(
            "@email",
            contact.Email);
        command.Parameters.AddWithValue(
            "@active",
            contact.Active);
        command.Parameters.AddWithValue(
            "@deleted",
            contact.Deleted);
        command.Parameters.AddWithValue(
            "@eventAt",
            contact.Timestamp);
        command.Parameters.AddWithValue(
            "@now",
            DateTime.UtcNow);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private sealed record ContactEvent(
        Guid UserId,
        string Email,
        bool Active,
        bool Deleted,
        DateTime Timestamp);
}