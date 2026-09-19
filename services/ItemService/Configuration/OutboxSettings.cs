namespace ItemService.Configuration;

/// <summary>
/// Tuning for the outbox relay. Bound from the optional "Outbox" config section;
/// every value has a default, so no appsettings / App Service change is needed.
/// </summary>
public sealed class OutboxSettings
{
    /// <summary>How often the relay looks for pending events when the queue is empty.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Maximum events claimed and published per cycle.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>After this many failed attempts an event is left in the table for manual inspection.</summary>
    public int MaxAttempts { get; set; } = 20;

    /// <summary>
    /// How long a claimed event is reserved for one relay. Must be longer than the producer's
    /// message timeout (30 s) so a slow batch is not claimed twice; if the app crashes mid-batch
    /// the events are picked up again once the lease expires.
    /// </summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Delivered events are deleted after this many days.</summary>
    public int RetentionDays { get; set; } = 7;
}
