namespace MatchingService.Configuration;

public sealed class ImageProcessingSettings
{
    public bool Enabled { get; init; } = false;

    public int PollIntervalSeconds { get; init; } = 5;

    public int RequestTimeoutSeconds { get; init; } = 60;

    public int LeaseSeconds { get; init; } = 180;

    public int MaxAttempts { get; init; } = 5;

    public int InitialRetrySeconds { get; init; } = 30;

    public int MaxRetrySeconds { get; init; } = 1800;
}