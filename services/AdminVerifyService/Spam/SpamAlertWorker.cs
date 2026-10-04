namespace AdminVerifyService.Spam;

public sealed class SpamAlertWorker(
    SpamAlertRepository repository,
    SpamAlertEmailSender sender,
    ILogger<SpamAlertWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

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
                        await Task.Delay(IdleDelay, stoppingToken);
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
                        "Spam alert processing failed. Code: SPAM_ALERT_CYCLE_FAILED. Type: {Type}.",
                        exception.GetType().Name);

                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DeliverAsync(SpamAlertJob job, CancellationToken stoppingToken)
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
                "Spam alert {NotificationId} failed on attempt {Attempt}. Type: {Type}.",
                job.Id, job.Attempts, exception.GetType().Name);
            return;
        }

        await repository.MarkSentAsync(job, stoppingToken);

        logger.LogInformation("Sent spam alert {NotificationId}.", job.Id);
    }
}
