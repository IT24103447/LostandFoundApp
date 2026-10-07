using MatchingService.Claims;

namespace MatchingService.Appeals;

public sealed class AppealService
{
    public const int MaxNoteLength = 300;

    private readonly ClaimService _claims;
    private readonly ClaimItemClient _items;
    private readonly AppealRepository _repository;

    public AppealService(
        ClaimService claims,
        ClaimItemClient items,
        AppealRepository repository)
    {
        _claims = claims;
        _items = items;
        _repository = repository;
    }

    public async Task<MyAppealView> SendAsync(
        SendAppealRequest request,
        Guid userId,
        string? email,
        string? phone,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewVersion))
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Preview the reports before sending an appeal.");
        }

        if (request.Note?.Trim().Length > MaxNoteLength)
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                $"The note can be at most {MaxNoteLength} characters.");
        }

        var preview = await _claims.PreviewAsync(
            new PairRequest(
                request.LostItemId,
                request.FoundItemId),
            userId,
            cancellationToken);

        if (!string.Equals(
                request.PreviewVersion,
                preview.PreviewVersion,
                StringComparison.Ordinal))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "The report information changed. Preview it again.");
        }

        if (preview.CanClaim)
        {
            throw new ClaimException(
                StatusCodes.Status422UnprocessableEntity,
                "This pair can be claimed directly.");
        }

        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(phone))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "Your contact details are unavailable. Please sign in again before sending an appeal.");
        }

        if (await _repository.PairHasAppealAsync(
                request.LostItemId,
                request.FoundItemId,
                userId,
                cancellationToken))
        {
            throw new ClaimException(
                StatusCodes.Status409Conflict,
                "This pair has already been submitted for appeal.");
        }

        var lost = await _items.GetAsync(
            "LOST",
            request.LostItemId,
            cancellationToken);

        var found = await _items.GetAsync(
            "FOUND",
            request.FoundItemId,
            cancellationToken);

        var appeal = await _repository.CreateAsync(
            new NewAppeal(
                lost.Id,
                found.Id,
                lost.UserId,
                found.UserId,
                userId,
                preview.ClaimantRole,
                email,
                phone,
                preview.Score,
                preview.Breakdown,
                preview.Lost,
                preview.Found,
                request.Note),
            cancellationToken);

        return ToMyView(appeal);
    }

    public async Task<List<MyAppealView>> GetMineAsync(
        Guid userId,
        int page,
        CancellationToken cancellationToken)
    {
        var appeals = await _repository.GetByAppellantAsync(
            userId,
            page,
            cancellationToken);

        return appeals.Select(ToMyView).ToList();
    }

    public Task<bool> PairHasAppealAsync(
        Guid lostItemId,
        Guid foundItemId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return _repository.PairHasAppealAsync(
            lostItemId,
            foundItemId,
            userId,
            cancellationToken);
    }

    public Task<bool> HasEditWarningAsync(
        string type,
        Guid itemId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var itemType = type.Trim().ToUpperInvariant();

        if (itemType is not ("LOST" or "FOUND"))
        {
            throw new ClaimException(
                StatusCodes.Status400BadRequest,
                "Type must be lost or found.");
        }

        return _repository.HasEditWarningAsync(
            itemType,
            itemId,
            userId,
            cancellationToken);
    }

    private static MyAppealView ToMyView(AppealRecord appeal) =>
        new(
            appeal.Id,
            appeal.Status,
            appeal.AppellantRole,
            appeal.Score,
            appeal.Lost,
            appeal.Found,
            appeal.Note,
            appeal.CreatedAt,
            appeal.DecidedAt,
            appeal.RejectionReason);
}
