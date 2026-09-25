using MatchingService.Claims;

namespace MatchingService.Matches;

public sealed class MatchReadService(IMatchReadRepository repository)
{
    private static readonly HashSet<string> Sections = new(StringComparer.Ordinal)
    {
        "active",
        "waiting-on-you",
        "waiting-on-other",
        "confirmed",
        "rejected",
        "deactivated",
        "closed",
        "all"
    };

    public async Task<MatchPage> GetPageAsync(
        Guid userId,
        string section,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var normalized = section.Trim().ToLowerInvariant();

        if (!Sections.Contains(normalized))
        {
            throw new ClaimException(400, "Select a valid matches section.");
        }

        if (page is < 1 or > 100000 || size is < 1 or > 100)
        {
            throw new ClaimException(
                400,
                "Page must be between 1 and 100000, and size between 1 and 100.");
        }

        var result = await repository.GetPageAsync(
            userId, normalized, page, size, cancellationToken);

        return new MatchPage(
            result.Items.Select(match => ToEntry(match, userId)).ToList(),
            page,
            size,
            result.TotalCount);
    }

    public async Task<MatchListEntry> GetByIdAsync(
        Guid matchId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var match = await repository.GetByIdAsync(matchId, cancellationToken);

        if (match is null)
        {
            throw new ClaimException(404, "Match not found.");
        }

        if (match.LostReporterId != userId && match.FinderId != userId)
        {
            throw new ClaimException(
                403, "You do not have permission to view this match.");
        }

        var normallyVisible = match.IsActive &&
            (match.Status is "LOST_REPORTER_CONFIRMED" or "FINDER_CONFIRMED"
                or "CONFIRMED" or "REJECTED");

        if (!normallyVisible && !IsVisibleDeactivatedMatch(match))
        {
            throw new ClaimException(404, "Match not found.");
        }

        return ToEntry(match, userId);
    }

    private static bool IsVisibleDeactivatedMatch(StoredMatch match) =>
        !match.IsActive &&
        (match.DeactivationReason is null or "ITEM_DELETED"
            or "ITEM_RESOLVED" or "MATCH_CONFIRMED_ELSEWHERE") &&
        (match.Status is "AWAITING_CLAIMANT_CONFIRMATION"
            or "LOST_REPORTER_CONFIRMED" or "FINDER_CONFIRMED");

    private static MatchListEntry ToEntry(StoredMatch match, Guid userId)
    {
        var lostReporter = match.LostReporterId == userId;
        var claimant = match.ClaimantId == userId;
        var deactivated = IsVisibleDeactivatedMatch(match);

        var yourTurn = match.IsActive &&
            ((lostReporter && match.Status == "FINDER_CONFIRMED") ||
             (!lostReporter && match.Status == "LOST_REPORTER_CONFIRMED"));

        var section = deactivated ? "deactivated" : match.Status switch
        {
            "CONFIRMED" => "confirmed",
            "REJECTED" => "rejected",
            _ => yourTurn ? "waiting-on-you" : "waiting-on-other"
        };

        return new MatchListEntry(
            match.Id,
            deactivated ? "DEACTIVATED" : match.Status,
            match.Score,
            match.CreatedAt,
            lostReporter ? "LOST" : "FOUND",
            claimant,
            claimant ? "You claimed this item" : "Someone claimed your item",
            section,
            yourTurn,
            lostReporter ? match.Found : match.Lost,
            match.Lost,
            match.Found)
        {
            DeactivatedAt = match.DeactivatedAt,
            DeactivationReason = match.DeactivationReason,
            DeactivatedItemId = match.DeactivatedItemId,
            DeactivatedItemType = match.DeactivatedItemType
        };
    }
}