using System.Net.Mail;

namespace MatchingService.Notifications;

public sealed class NotificationDeliveryService(
    NotificationRepository repository,
    IMatchEmailSender sender,
    ILogger<NotificationDeliveryService> logger)
{
    public async Task DeliverAsync(
        NotificationJob job,
        CancellationToken cancellationToken)
    {
        var recipient = await repository.GetRecipientAsync(
            job, cancellationToken);

        if (!recipient.CanSend)
        {
            await repository.MarkCancelledAsync(
                job,
                recipient.SuppressionCode ?? "NOTIFICATION_SUPPRESSED",
                cancellationToken);

            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                throw new NotificationDeliveryException(
                    "RECIPIENT_EMAIL_UNAVAILABLE");
            }

            await sender.SendAsync(
                job, recipient.Email, cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                NotificationDeliveryException known => known.Code,
                SmtpException smtp => $"SMTP_ERROR_{(int)smtp.StatusCode}",
                _ => "NOTIFICATION_SEND_FAILED"
            };

            var recorded = await repository.MarkFailedAsync(
                job, code, cancellationToken);

            logger.LogWarning(
                "Notification {NotificationId} attempt {Attempt} failed. " +
                "Code: {Code}. Type: {Type}. Result saved: {Saved}.",
                job.Id,
                job.Attempts,
                code,
                exception.GetType().Name,
                recorded);

            return;
        }

        try
        {
            var recorded = await repository.MarkSentAsync(
                job, cancellationToken);

            if (recorded)
            {
                logger.LogInformation(
                    "Notification {NotificationId} accepted by SMTP and marked SENT.",
                    job.Id);
            }
            else
            {
                logger.LogError(
                    "Notification {NotificationId} was accepted by SMTP, " +
                    "but its delivery record was not updated. " +
                    "Code: SMTP_ACCEPTED_STATE_NOT_SAVED.",
                    job.Id);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // SMTP acceptance cannot be rolled back with a database transaction.
            // Leave the lease to expire instead of mislabelling this as send failure.
            logger.LogError(
                "Notification {NotificationId} was accepted by SMTP, " +
                "but saving SENT failed. " +
                "Code: SMTP_ACCEPTED_STATE_NOT_SAVED. Type: {Type}.",
                job.Id,
                exception.GetType().Name);
        }
    }
}