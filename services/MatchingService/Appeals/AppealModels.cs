using MatchingService.Claims;

namespace MatchingService.Appeals;

public static class AppealStatus
{
    public const string Pending = "PENDING";
    public const string Verified = "VERIFIED";
    public const string Rejected = "REJECTED";
}

public sealed record NewAppeal(
    Guid LostItemId,
    Guid FoundItemId,
    Guid LostReporterId,
    Guid FinderId,
    Guid AppellantId,
    string AppellantRole,
    string AppellantEmail,
    string AppellantPhone,
    decimal Score,
    ScoreBreakdown Breakdown,
    ClaimItemView Lost,
    ClaimItemView Found,
    string? Note);

public sealed record AppealRecord(
    Guid Id,
    Guid LostItemId,
    Guid FoundItemId,
    Guid LostReporterId,
    Guid FinderId,
    Guid AppellantId,
    string AppellantRole,
    string AppellantEmail,
    string AppellantPhone,
    decimal Score,
    ScoreBreakdown Breakdown,
    ClaimItemView Lost,
    ClaimItemView Found,
    string? Note,
    string Status,
    Guid? DecidedBy,
    DateTime? DecidedAt,
    DateTime CreatedAt);
