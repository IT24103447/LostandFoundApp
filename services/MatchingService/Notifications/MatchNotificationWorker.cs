namespace MatchingService.Notifications;

public sealed class MatchNotificationWorker(
    NotificationDiscoveryService discovery,
    NotificationRepository repository,
    NotificationDeliveryService delivery,
    ILogger<MatchNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        var nextDiscoveryAt = DateTime.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= nextDiscoveryAt)
                    {
                        await discovery.DiscoverAsync(stoppingToken);

                        nextDiscoveryAt =
                            DateTime.UtcNow.AddSeconds(15);
                    }

                    var job = await repository.ClaimNextAsync(stoppingToken);

                    if (job is null)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(5), stoppingToken);

                        continue;
                    }

                    await delivery.DeliverAsync(job, stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        "Notification processing failed. " +
                        "Code: NOTIFICATION_CYCLE_FAILED. Type: {Type}.",
                        exception.GetType().Name);

                    await Task.Delay(
                        TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}