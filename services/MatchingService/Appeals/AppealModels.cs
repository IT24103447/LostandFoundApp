using MatchingService.Claims;

namespace MatchingService.Appeals;

public static class AppealStatus
{
    public const string Pending = "PENDING";
    public const string Verified = "VERIFIED";
    public const string Rejected = "REJECTED";
}

public static class AppealNotificationType
{
    public const string Verified = "APPEAL_VERIFIED";
    public const string Rejected = "APPEAL_REJECTED";
}

public sealed record SendAppealRequest(
    Guid LostItemId,
    Guid FoundItemId,
    string PreviewVersion,
    string? Note);

public sealed record RejectAppealRequest(
    string? Reason);

public sealed record MyAppealView(
    Guid Id,
    string Status,
    string Role,
    decimal Score,
    ClaimItemView Lost,
    ClaimItemView Found,
    string? Note,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string? RejectionReason);

public sealed record AdminAppealView(
    Guid Id,
    string Status,
    Guid AppellantId,
    string AppellantRole,
    Guid LostReporterId,
    Guid FinderId,
    decimal Score,
    ScoreBreakdown Breakdown,
    ClaimItemView Lost,
    ClaimItemView Found,
    string? Note,
    DateTime CreatedAt,
    Guid? DecidedBy,
    DateTime? DecidedAt,
    string? RejectionReason);

public sealed record AppealCurrentScore(
    decimal Score,
    ScoreBreakdown Breakdown,
    ClaimItemView Lost,
    ClaimItemView Found);

public sealed record AdminAppealDetail(
    AdminAppealView Appeal,
    AppealCurrentScore? Current,
    string? CurrentUnavailableReason);

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
    DateTime CreatedAt,
    string? RejectionReason);
