using System.Text.Json;
using ItemService.Databases;
using ItemService.Models.Events;
using MySqlConnector;

namespace ItemService.Services;

/// <summary>
/// IEventPublisher for production: instead of sending to Kafka, it writes the event to the
/// outbox_events table on the request's database transaction (see RequestTransactionFilter).
/// OutboxRelayService delivers it to Kafka afterwards.
///
/// Unlike the old fire-and-forget publisher this DOES throw if the row cannot be written. That is
/// deliberate: the item change is on the same transaction, so a failure rolls both back and the
/// caller gets an error they can retry, instead of a saved item whose event silently vanished.
/// </summary>
public sealed class OutboxEventPublisher : IEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IDbSession _session;
    private readonly ILogger<OutboxEventPublisher> _logger;

    public OutboxEventPublisher(IDbSession session, ILogger<OutboxEventPublisher> logger)
    {
        _session = session;
        _logger = logger;
    }

    public async ValueTask PublishAsync<T>(string topic, T payload, CancellationToken ct = default)
    {
        // Events already carry a unique EventId. Reuse it as the outbox row id and the Kafka message
        // key so consumers can de-duplicate (delivery is at-least-once, so a retry may repeat an event).
        var eventId = payload is BaseEvent baseEvent ? baseEvent.EventId : Guid.NewGuid();
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO outbox_events
                (id, topic, message_key, payload, created_at, next_attempt_at)
            VALUES
                (@id, @topic, @messageKey, @payload, @now, @now);
            """;

        await using var lease = await _session.AcquireAsync(ct);
        await using var cmd = new MySqlCommand(sql, lease.Connection, lease.Transaction);
        cmd.Parameters.AddWithValue("@id", eventId.ToString());
        cmd.Parameters.AddWithValue("@topic", topic);
        cmd.Parameters.AddWithValue("@messageKey", eventId.ToString());
        cmd.Parameters.AddWithValue("@payload", json);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);

        // Never log the payload: it carries HiddenInformation.
        _logger.LogDebug("Recorded event {EventType} ({EventId}) for topic {Topic} in the outbox.",
            typeof(T).Name, eventId, topic);
    }
}
