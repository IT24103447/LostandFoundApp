namespace MatchingService.Models;

public sealed class GeneratedImageDescription
{
    public string Description { get; init; } = string.Empty;

    public string? ObjectType { get; init; }

    public string[] Colours { get; init; } = [];

    public string[] Materials { get; init; } = [];

    public string? VisibleBrand { get; init; }

    public string[] Patterns { get; init; } = [];

    public string? Condition { get; init; }

    public string[] DistinctiveFeatures { get; init; } = [];

    public string[] Uncertainty { get; init; } = [];
}