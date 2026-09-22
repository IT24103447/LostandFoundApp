using MatchingService.Claims;

namespace MatchingService.Matches;

public sealed class MatchReadService
{
    private static readonly HashSet<string> Sections =
        new(StringComparer.Ordinal)
        {
            "active",
            "waiting-on-you",
            "waiting-on-other",
            "confirmed",
            "rejected",
            "closed",
            "all"
        };

    private readonly IMatchReadRepository _repository;

    public MatchReadService(IMatchReadRepository repository)
    {
        _repository = repository;
    }

    public async Task<MatchPage> GetPageAsync(
        Guid userId,
        string section,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var normalizedSection = section.Trim().ToLowerInvariant();

        if (!Sections.Contains(normalizedSection))
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Select a valid matches section.");
        }

        if (page is < 1 or > 100000 ||
            size is < 1 or > 100)
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Page must be between 1 and 100000, " +
                "and size must be between 1 and 100.");
        }

        var result = await _repository.GetPageAsync(
            userId,
            normalizedSection,
            page,
            size,
            cancellationToken);

        var entries = result.Items
            .Select(match => ToEntry(match, userId))
            .ToList();

        return new MatchPage(
            entries,
            page,
            size,
            result.TotalCount);
    }

    public async Task<MatchListEntry> GetByIdAsync(
        Guid matchId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var match = await _repository.GetByIdAsync(
            matchId,
            cancellationToken);

        if (match is null)
        {
            throw new ClaimException(
                StatusCodes.Status404NotFound,
                "Match not found.");
        }

        if (match.LostReporterId != userId &&
            match.FinderId != userId)
        {
            throw new ClaimException(
                StatusCodes.Status403Forbidden,
                "You do not have permission to view this match.");
        }

        if (!match.IsActive ||
            !IsVisibleStatus(match.Status))
        {
            throw new ClaimException(
                StatusCodes.Status404NotFound,
                "Match not found.");
        }

        return ToEntry(match, userId);
    }

    private static bool IsVisibleStatus(string status)
    {
        return status is
            "LOST_REPORTER_CONFIRMED" or
            "FINDER_CONFIRMED" or
            "CONFIRMED" or
            "REJECTED";
    }

    private static MatchListEntry ToEntry(
        StoredMatch match,
        Guid userId)
    {
        var isLostReporter = match.LostReporterId == userId;
        var isClaimant = match.ClaimantId == userId;

        var isYourTurn =
            (isLostReporter &&
             match.Status == "FINDER_CONFIRMED") ||
            (!isLostReporter &&
             match.Status == "LOST_REPORTER_CONFIRMED");

        var section = match.Status switch
        {
            "CONFIRMED" => "confirmed",
            "REJECTED" => "rejected",
            _ => isYourTurn
                ? "waiting-on-you"
                : "waiting-on-other"
        };

        return new MatchListEntry(
            match.Id,
            match.Status,
            match.Score,
            match.CreatedAt,
            isLostReporter ? "LOST" : "FOUND",
            isClaimant,
            isClaimant
                ? "You claimed this item"
                : "Someone claimed your item",
            section,
            isYourTurn,
            isLostReporter ? match.Found : match.Lost,
            match.Lost,
            match.Found);
    }
}