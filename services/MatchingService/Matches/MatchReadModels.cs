using MatchingService.Claims;

namespace MatchingService.Matches;

public sealed record MatchListEntry(
    Guid Id,
    string Status,
    decimal Score,
    DateTime CreatedAt,
    string YourRole,
    bool IsClaimant,
    string RoleLabel,
    string Section,
    bool IsYourTurn,
    ClaimItemView OtherItem,
    ClaimItemView Lost,
    ClaimItemView Found)
{
    public DateTime? DeactivatedAt { get; init; }
    public string? DeactivationReason { get; init; }
    public Guid? DeactivatedItemId { get; init; }
    public string? DeactivatedItemType { get; init; }
}

public sealed record MatchPage(
    IReadOnlyList<MatchListEntry> Items,
    int Page,
    int Size,
    long TotalCount);

public sealed record StoredMatch(
    Guid Id,
    Guid LostReporterId,
    Guid FinderId,
    Guid ClaimantId,
    string Status,
    bool IsActive,
    decimal Score,
    DateTime CreatedAt,
    ClaimItemView Lost,
    ClaimItemView Found)
{
    public DateTime? DeactivatedAt { get; init; }
    public string? DeactivationReason { get; init; }
    public Guid? DeactivatedItemId { get; init; }
    public string? DeactivatedItemType { get; init; }
}

public sealed record StoredMatchPage(
    IReadOnlyList<StoredMatch> Items,
    long TotalCount);

public interface IMatchReadRepository
{
    Task<StoredMatchPage> GetPageAsync(
        Guid userId,
        string section,
        int page,
        int size,
        CancellationToken cancellationToken);

    Task<StoredMatch?> GetByIdAsync(
        Guid matchId,
        CancellationToken cancellationToken);
}