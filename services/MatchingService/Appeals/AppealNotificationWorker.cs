namespace MatchingService.Appeals;

public sealed class AppealNotificationWorker(
    AppealNotificationRepository repository,
    IAppealEmailSender sender,
    ILogger<AppealNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var job = await repository.ClaimNextAsync(stoppingToken);

                    if (job is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }

                    await DeliverAsync(job, stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        "Appeal email processing failed. Code: APPEAL_EMAIL_CYCLE_FAILED. Type: {Type}.",
                        exception.GetType().Name);

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DeliverAsync(AppealEmailJob job, CancellationToken stoppingToken)
    {
        try
        {
            await sender.SendAsync(job, stoppingToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            await repository.MarkFailedAttemptAsync(job, stoppingToken);

            logger.LogWarning(
                "Appeal email {NotificationId} failed on attempt {Attempt}. Type: {Type}.",
                job.Id, job.Attempts, exception.GetType().Name);
            return;
        }

        await repository.MarkSentAsync(job, stoppingToken);

        logger.LogInformation(
            "Sent {Type} appeal email {NotificationId}.",
            job.Type, job.Id);
    }
}
