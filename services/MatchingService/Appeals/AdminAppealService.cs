using MatchingService.Claims;

namespace MatchingService.Appeals;

public sealed class AdminAppealService
{
    public const string ReportInactive = "REPORT_INACTIVE";
    public const string ScoreUnavailable = "SCORE_UNAVAILABLE";

    private static readonly string[] Statuses =
    [
        AppealStatus.Pending,
        AppealStatus.Verified,
        AppealStatus.Rejected
    ];

    private readonly AppealRepository _appeals;
    private readonly ClaimService _claims;
    private readonly ClaimRepository _matches;
    private readonly ClaimItemClient _items;

    public AdminAppealService(
        AppealRepository appeals,
        ClaimService claims,
        ClaimRepository matches,
        ClaimItemClient items)
    {
        _appeals = appeals;
        _claims = claims;
        _matches = matches;
        _items = items;
    }

    public async Task<List<AdminAppealView>> ListAsync(
        string status,
        int page,
        CancellationToken cancellationToken)
    {
        var normalized = status.Trim().ToUpperInvariant();

        if (!Statuses.Contains(normalized))
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Status must be PENDING, VERIFIED or REJECTED.");
        }

        var appeals = await _appeals.ListByStatusAsync(
            normalized,
            page,
            cancellationToken);

        return appeals.Select(ToAdminView).ToList();
    }

    public async Task<AdminAppealDetail> OpenAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var appeal = await GetRequiredAsync(id, cancellationToken);
        var view = ToAdminView(appeal);

        if (appeal.Status != AppealStatus.Pending)
        {
            return new AdminAppealDetail(view, null, null);
        }

        ItemReport lost;
        ItemReport found;

        try
        {
            (lost, found) = await GetActivePairAsync(appeal, cancellationToken);
        }
        catch (ClaimException exception)
            when (exception.StatusCode == StatusCodes.Status409Conflict)
        {
            return new AdminAppealDetail(view, null, ReportInactive);
        }

        try
        {
            var preview = await _claims.ScoreExistingAsync(
                lost,
                found,
                cancellationToken);

            return new AdminAppealDetail(
                view,
                new AppealCurrentScore(
                    preview.Score,
                    preview.Breakdown,
                    preview.Lost,
                    preview.Found),
                null);
        }
        catch (ClaimException exception)
            when (exception.StatusCode == StatusCodes.Status409Conflict)
        {
            return new AdminAppealDetail(view, null, ScoreUnavailable);
        }
    }

    public async Task<AdminAppealView> VerifyAsync(
        Guid id,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        var appeal = await GetRequiredAsync(id, cancellationToken);
        EnsurePending(appeal);

        var (lost, found) = await GetActivePairAsync(appeal, cancellationToken);

        if (await _matches.PairExistsAsync(
                lost.Id,
                found.Id,
                cancellationToken))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "This pair, or one of its reports, already has a match.");
        }

        var preview = await _claims.ScoreExistingAsync(
            lost,
            found,
            cancellationToken);

        if (!await _appeals.DecideAsync(
                id,
                AppealStatus.Verified,
                adminId,
                cancellationToken))
        {
            throw AlreadyDecided();
        }

        var appellantIsFinder = appeal.AppellantRole == "FOUND";

        try
        {
            await _matches.CreateAsync(
                new VerifiedPair(lost, found, appeal.AppellantRole),
                preview,
                appeal.AppellantId,
                cancellationToken,
                appellantIsFinder ? appeal.AppellantEmail : null,
                appellantIsFinder ? appeal.AppellantPhone : null,
                appellantIsFinder ? null : appeal.AppellantEmail,
                appellantIsFinder ? null : appeal.AppellantPhone);
        }
        catch
        {
            await _appeals.ReopenAsync(id, CancellationToken.None);
            throw;
        }

        return ToAdminView(await GetRequiredAsync(id, cancellationToken));
    }

    public async Task<AdminAppealView> RejectAsync(
        Guid id,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        var appeal = await GetRequiredAsync(id, cancellationToken);
        EnsurePending(appeal);

        if (!await _appeals.DecideAsync(
                id,
                AppealStatus.Rejected,
                adminId,
                cancellationToken))
        {
            throw AlreadyDecided();
        }

        return ToAdminView(await GetRequiredAsync(id, cancellationToken));
    }

    private async Task<AppealRecord> GetRequiredAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await _appeals.GetAsync(id, cancellationToken)
            ?? throw new ClaimException(
                StatusCodes.Status404NotFound,
                "Appeal not found.");
    }

    private async Task<(ItemReport Lost, ItemReport Found)> GetActivePairAsync(
        AppealRecord appeal,
        CancellationToken cancellationToken)
    {
        try
        {
            var lost = await _items.GetAsync(
                "LOST",
                appeal.LostItemId,
                cancellationToken);

            var found = await _items.GetAsync(
                "FOUND",
                appeal.FoundItemId,
                cancellationToken);

            if (IsActive(lost) && IsActive(found))
            {
                return (lost, found);
            }
        }
        catch (ClaimException exception)
            when (exception.StatusCode == StatusCodes.Status404NotFound)
        {
            // A deleted report is treated the same as a resolved one below.
        }

        throw new ClaimException(
            StatusCodes.Status409Conflict,
            "A report in this appeal has been resolved or deleted.");
    }

    private static bool IsActive(ItemReport report) =>
        string.Equals(
            report.Status,
            "ACTIVE",
            StringComparison.OrdinalIgnoreCase);

    private static void EnsurePending(AppealRecord appeal)
    {
        if (appeal.Status != AppealStatus.Pending)
        {
            throw AlreadyDecided();
        }
    }

    private static ClaimException AlreadyDecided() =>
        new(
            StatusCodes.Status409Conflict,
            "This appeal has already been decided.");

    private static AdminAppealView ToAdminView(AppealRecord appeal) =>
        new(
            appeal.Id,
            appeal.Status,
            appeal.AppellantId,
            appeal.AppellantRole,
            appeal.LostReporterId,
            appeal.FinderId,
            appeal.Score,
            appeal.Breakdown,
            appeal.Lost,
            appeal.Found,
            appeal.Note,
            appeal.CreatedAt,
            appeal.DecidedBy,
            appeal.DecidedAt);
}
