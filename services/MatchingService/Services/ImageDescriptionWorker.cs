using MatchingService.Configuration;
using MatchingService.Models;
using MatchingService.Repositories;
using Microsoft.Extensions.Options;

namespace MatchingService.Services;

public sealed class ImageDescriptionWorker : BackgroundService
{
    private readonly IImageDescriptionRepository _repository;
    private readonly IImageDescriptionGenerator _generator;
    private readonly ImageProcessingSettings _settings;
    private readonly GeminiSettings _gemini;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImageDescriptionWorker> _logger;

    public ImageDescriptionWorker(
        IImageDescriptionRepository repository,
        IImageDescriptionGenerator generator,
        IOptions<ImageProcessingSettings> settings,
        IOptions<GeminiSettings> gemini,
        TimeProvider timeProvider,
        ILogger<ImageDescriptionWorker> logger)
    {
        _repository = repository;
        _generator = generator;
        _settings = settings.Value;
        _gemini = gemini.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _repository.ClaimNextAsync(
                    _settings.MaxAttempts,
                    _settings.LeaseSeconds,
                    stoppingToken);

                if (job is null)
                {
                    await DelayAsync(stoppingToken);
                    continue;
                }

                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Provider/database exception messages can contain URLs or
                // connection details. Log only a controlled diagnostic code.
                _logger.LogError(
                    "Image processing cycle failed: {ErrorCode}.",
                    "WORKER_CYCLE_FAILED");

                try
                {
                    await DelayAsync(stoppingToken);
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
        ClaimedImageDescription job,
        CancellationToken stoppingToken)
    {
        GeneratedImageDescription description;

        try
        {
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(stoppingToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(
                _settings.RequestTimeoutSeconds));

            description = await _generator
                .GenerateAsync(job.BlobUrl, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Leave the lease recoverable when the application stops.
            throw;
        }
        catch (OperationCanceledException)
        {
            await FailAsync(
                job,
                "AI_TIMEOUT",
                retryable: true,
                stoppingToken);

            return;
        }
        catch (ImageProcessingException exception)
        {
            await FailAsync(
                job,
                exception.Code,
                exception.Retryable,
                stoppingToken);

            return;
        }

        catch (Exception exception)
        {
            var exceptionType = exception.GetType();

            var statusCode = exceptionType
                .GetProperty("StatusCode")
                ?.GetValue(exception);

            var status = exceptionType
                .GetProperty("Status")
                ?.GetValue(exception);

            _logger.LogWarning(
                "Gemini request failed. Exception: {ExceptionType}, HTTP: {StatusCode}, Status: {Status}.",
                exceptionType.FullName,
                statusCode,
                status);

            await FailAsync(
                job,
                "AI_REQUEST_FAILED",
                retryable: true,
                stoppingToken);

            return;
        }

        var completed = await _repository.CompleteAsync(
            job,
            description,
            _gemini.Model,
            stoppingToken);

        if (completed)
        {
            _logger.LogInformation(
                "Image description {DescriptionId} completed.",
                job.Id);
        }
        else
        {
            _logger.LogWarning(
                "Image description {DescriptionId} no longer owns its lease.",
                job.Id);
        }
    }

    private async Task FailAsync(
        ClaimedImageDescription job,
        string errorCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        DateTime? nextRetryAt = null;

        if (retryable && job.Attempts < _settings.MaxAttempts)
        {
            var delaySeconds = Math.Min(
                _settings.MaxRetrySeconds,
                _settings.InitialRetrySeconds *
                Math.Pow(2, job.Attempts - 1));

            nextRetryAt = _timeProvider.GetUtcNow()
                .UtcDateTime
                .AddSeconds(delaySeconds);
        }

        var recorded = await _repository.RecordFailureAsync(
            job,
            errorCode,
            nextRetryAt,
            cancellationToken);

        if (recorded)
        {
            _logger.LogWarning(
                """
                Image description {DescriptionId} attempt {Attempt} failed.
                Code: {ErrorCode}. Retry scheduled: {RetryScheduled}.
                """,
                job.Id,
                job.Attempts,
                errorCode,
                nextRetryAt.HasValue);
        }
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(
            TimeSpan.FromSeconds(_settings.PollIntervalSeconds),
            cancellationToken);
}