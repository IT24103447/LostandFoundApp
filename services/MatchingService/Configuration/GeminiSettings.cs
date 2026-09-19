namespace MatchingService.Configuration;

public sealed class GeminiSettings
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
}
