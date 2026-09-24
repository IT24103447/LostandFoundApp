namespace MatchingService.Claims;

public sealed record PairRequest(
    Guid LostItemId,
    Guid FoundItemId);

public sealed record SubmitClaimRequest(
    Guid LostItemId,
    Guid FoundItemId,
    string PreviewVersion);

public sealed record ClaimItemView(
    Guid Id,
    string Type,
    string Title,
    string Category,
    string Description,
    string Date,
    string Location,
    string? PhotoUrl = null,
    bool PhotoSnapshotCaptured = false);

public sealed record ScoreBreakdown(
    decimal Title,
    decimal Category,
    decimal Description,
    decimal ImageDescription,
    decimal ImageAttributes);

public sealed record ClaimPreview(
    ClaimItemView Lost,
    ClaimItemView Found,
    decimal Score,
    decimal Threshold,
    bool CanClaim,
    string ClaimantRole,
    string PreviewVersion,
    ScoreBreakdown Breakdown);

public sealed record MatchView(
    Guid Id,
    string Status,
    string ClaimantRole,
    decimal Score,
    DateTime CreatedAt,
    ClaimItemView Lost,
    ClaimItemView Found);

public sealed record VerifiedPair(
    ItemReport Lost,
    ItemReport Found,
    string ClaimantRole);

public sealed record ImageEvidence(
    Guid DescriptionId,
    string Description,
    string AttributesJson);

public sealed class ClaimException : Exception
{
    public int StatusCode { get; }

    public ClaimException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

public sealed class ItemReport
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string DateLost { get; set; } = string.Empty;

    public string DateFound { get; set; } = string.Empty;

    public string LastKnownLocation { get; set; } = string.Empty;

    public string LocationFound { get; set; } = string.Empty;

    public List<string> PhotoUrls { get; set; } = [];

    public ClaimItemView ToView(string type)
    {
        return new ClaimItemView(
            Id,
            type,
            Title,
            Category,
            Description,
            type == "LOST" ? DateLost : DateFound,
            type == "LOST"
                ? LastKnownLocation
                : LocationFound,
            PhotoUrls.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)),
            true);
    }
}
