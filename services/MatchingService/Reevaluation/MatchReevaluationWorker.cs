using MatchingService.Claims;

namespace MatchingService.Reevaluation;

public sealed class MatchReevaluationWorker(
    MatchReevaluationRepository repository,
    IServiceScopeFactory scopes,
    ILogger<MatchReevaluationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            MatchReevaluationJob? job = null;

            try
            {
                job = await repository.ClaimNextAsync(
                    stoppingToken);

                if (job is null)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);

                    continue;
                }

                await ProcessAsync(
                    job,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Match re-evaluation failed. Code: {Code}. Type: {Type}.",
                    "REEVALUATION_FAILED",
                    exception.GetType().FullName);

                if (job is not null)
                {
                    try
                    {
                        await repository.FailAsync(
                            job,
                            "REEVALUATION_FAILED",
                            retryable: true,
                            stoppingToken);
                    }
                    catch (Exception saveException)
                    {
                        logger.LogError(
                            "Could not record re-evaluation failure. Type: {Type}.",
                            saveException.GetType().FullName);
                    }
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessAsync(
        MatchReevaluationJob job,
        CancellationToken cancellationToken)
    {
        if (!await repository.IsCurrentAsync(
            job,
            cancellationToken))
        {
            await repository.CompleteAsync(
                job,
                cancellationToken);

            return;
        }

        var pairs = await repository.GetActivePairsAsync(
            job,
            cancellationToken);

        using var scope = scopes.CreateScope();

        var claims = scope.ServiceProvider
            .GetRequiredService<ClaimService>();

        foreach (var pair in pairs)
        {
            if (!await repository.IsCurrentAsync(
                job,
                cancellationToken))
            {
                break;
            }

            var lost = await repository.GetLatestReportAsync(
                "LOST",
                pair.LostItemId,
                pair.LostReporterId,
                pair.LostSnapshotJson,
                cancellationToken);

            var found = await repository.GetLatestReportAsync(
                "FOUND",
                pair.FoundItemId,
                pair.FinderId,
                pair.FoundSnapshotJson,
                cancellationToken);

            // Resolved and deleted item events have their own
            // deactivation flow.
            if (!string.Equals(
                    lost.Status,
                    "ACTIVE",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    found.Status,
                    "ACTIVE",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var changedReport = job.ItemType == "LOST"
                ? lost
                : found;

            var changedReadiness =
                await repository.GetPhotoReadinessAsync(
                    changedReport,
                    job.ItemType,
                    cancellationToken);

            var bothHavePhotos =
                HasPhoto(lost) && HasPhoto(found);

            var otherReadiness = bothHavePhotos
                ? await repository.GetPhotoReadinessAsync(
                    job.ItemType == "LOST"
                        ? found
                        : lost,
                    job.ItemType == "LOST"
                        ? "FOUND"
                        : "LOST",
                    cancellationToken)
                : PhotoReadiness.Ready;

            if (changedReadiness == PhotoReadiness.Failed ||
                otherReadiness == PhotoReadiness.Failed)
            {
                await repository.FailAsync(
                    job,
                    "IMAGE_DESCRIPTION_FAILED",
                    retryable: false,
                    cancellationToken);

                return;
            }

            if (changedReadiness == PhotoReadiness.Pending ||
                otherReadiness == PhotoReadiness.Pending)
            {
                if (DateTime.UtcNow - job.CreatedAt >
                    TimeSpan.FromHours(1))
                {
                    await repository.FailAsync(
                        job,
                        "IMAGE_DESCRIPTION_UNAVAILABLE",
                        retryable: false,
                        cancellationToken);
                }
                else
                {
                    await repository.DeferAsync(
                        job,
                        cancellationToken);
                }

                return;
            }

            ClaimPreview preview;

            try
            {
                preview = await claims.ScoreExistingAsync(
                    lost,
                    found,
                    cancellationToken);
            }
            catch (ClaimException exception)
                when (exception.StatusCode == 409)
            {
                await repository.DeferAsync(
                    job,
                    cancellationToken);

                return;
            }

            await repository.ApplyScoreAsync(
                job,
                pair,
                lost,
                found,
                preview.Score,
                cancellationToken);
        }

        await repository.CompleteAsync(
            job,
            cancellationToken);
    }

    private static bool HasPhoto(ItemReport report) =>
        report.PhotoUrls?.Any(
            url => !string.IsNullOrWhiteSpace(url)) == true;
}