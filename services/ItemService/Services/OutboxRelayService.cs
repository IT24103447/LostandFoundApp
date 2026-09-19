using Confluent.Kafka;
using ItemService.Configuration;
using Microsoft.Extensions.Options;

namespace ItemService.Services;

/// <summary>
/// Delivers events from the outbox_events table to Kafka.
///
/// Delivery is AT-LEAST-ONCE: an event is only marked published after the broker acknowledged it,
/// so a crash between "broker acknowledged" and "marked published" sends the event again later.
/// Consumers must de-duplicate on EventId (also the Kafka message key).
///
/// A broker outage therefore no longer loses anything: failed events stay in the table and are
/// retried with exponential backoff until the broker is back.
/// </summary>
public sealed class OutboxRelayService : BackgroundService
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IOutboxStore _store;
    private readonly IProducer<string, string> _producer;
    private readonly OutboxSettings _settings;
    private readonly ILogger<OutboxRelayService> _logger;
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    public OutboxRelayService(
        IOutboxStore store,
        IProducer<string, string> producer,
        IOptions<OutboxSettings> settings,
        ILogger<OutboxRelayService> logger)
    {
        _store = store;
        _producer = producer;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox relay started (batch {BatchSize}, poll {PollMs} ms).",
            _settings.BatchSize, _settings.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(_settings.PollIntervalMs);

            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed >= _settings.BatchSize)
                {
                    delay = TimeSpan.Zero; // a full batch means more is probably waiting
                }

                await PurgeIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Typically the database is unreachable or the outbox table is missing.
                // An escaping exception would stop the whole host, so log and try again.
                _logger.LogError(ex, "Outbox relay cycle failed; retrying in 10 seconds.");
                delay = TimeSpan.FromSeconds(10);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Outbox relay stopped.");
    }

    /// <summary>Claims one batch and publishes it. Returns how many events were claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var batch = await _store.ClaimBatchAsync(
            _settings.BatchSize,
            _settings.MaxAttempts,
            TimeSpan.FromSeconds(_settings.LeaseSeconds),
            DateTime.UtcNow,
            ct);

        if (batch.Count == 0)
        {
            return 0;
        }

        // Publish the whole batch concurrently so one slow or failing event cannot delay the others.
        await Task.WhenAll(batch.Select(record => DeliverAsync(record, ct)));
        return batch.Count;
    }

    private async Task DeliverAsync(OutboxRecord record, CancellationToken ct)
    {
        try
        {
            // ProduceAsync completes only when the broker has acknowledged the message (or it timed out).
            var result = await _producer.ProduceAsync(
                record.Topic,
                new Message<string, string> { Key = record.MessageKey, Value = record.Payload },
                ct);

            _logger.LogDebug("Published outbox event {EventId} to {Topic} (partition {Partition}, offset {Offset}).",
                record.Id, result.Topic, result.Partition, result.Offset);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. Leave the row reserved; it is retried once the lease expires.
            return;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(record, ex);
            return;
        }

        try
        {
            await _store.MarkPublishedAsync(record.Id, DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Kafka has the event but the table does not know yet. The row is retried after the lease
            // expires, which repeats the event: harmless because consumers de-duplicate on EventId.
            _logger.LogWarning(ex,
                "Outbox event {EventId} was delivered to {Topic} but could not be marked published; it will be sent again.",
                record.Id, record.Topic);
        }
    }

    private async Task RecordFailureAsync(OutboxRecord record, Exception ex)
    {
        var attempts = record.Attempts + 1;
        var reason = ex is ProduceException<string, string> produceEx ? produceEx.Error.Reason : ex.Message;

        try
        {
            await _store.MarkFailedAsync(
                record.Id,
                Truncate(reason, 500),
                DateTime.UtcNow + ComputeBackoff(attempts),
                CancellationToken.None);
        }
        catch (Exception storeEx)
        {
            _logger.LogWarning(storeEx, "Could not record the failed attempt for outbox event {EventId}.", record.Id);
        }

        if (attempts >= _settings.MaxAttempts)
        {
            _logger.LogError(ex,
                "Outbox event {EventId} for topic {Topic} failed {Attempts} times and will not be retried automatically. " +
                "Check last_error in outbox_events and reset attempts to 0 to retry.",
                record.Id, record.Topic, attempts);
        }
        else
        {
            _logger.LogWarning(
                "Outbox event {EventId} for topic {Topic} failed to publish (attempt {Attempts}): {Reason}",
                record.Id, record.Topic, attempts, reason);
        }
    }

    private async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPurgeUtc < PurgeInterval)
        {
            return;
        }

        _lastPurgeUtc = now;
        var removed = await _store.PurgePublishedAsync(now.AddDays(-_settings.RetentionDays), ct);
        if (removed > 0)
        {
            _logger.LogInformation("Purged {Count} delivered outbox event(s) older than {Days} days.",
                removed, _settings.RetentionDays);
        }
    }

    /// <summary>5 s, 10 s, 20 s ... capped at 5 minutes.</summary>
    public static TimeSpan ComputeBackoff(int attempts)
    {
        var seconds = 5 * Math.Pow(2, Math.Min(Math.Max(attempts, 1), 10) - 1);
        var backoff = TimeSpan.FromSeconds(seconds);
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
