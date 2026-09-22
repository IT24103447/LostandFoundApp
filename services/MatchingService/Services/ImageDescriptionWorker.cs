using MatchingService.Configuration;
using MatchingService.Models;
using MatchingService.Repositories;
using Microsoft.Extensions.Options;
using System.Net;
using System.Reflection;

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
            catch (Exception exception)
            {
                _logger.LogError(
                    """
                    Image processing cycle failed.
                    Error code: {ErrorCode}.
                    Exception type: {ExceptionType}.
                    Inner exception type: {InnerExceptionType}.
                    """,
                    "WORKER_CYCLE_FAILED",
                    exception.GetType().FullName,
                    exception.InnerException?.GetType().FullName);

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
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            timeout.CancelAfter(TimeSpan.FromSeconds(
                _settings.RequestTimeoutSeconds));

            description = await _generator.GenerateAsync(
                job.BlobUrl,
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
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
            var failure = DescribeGeminiFailure(exception);

            _logger.LogWarning(
                """
                Gemini request failed.
                Error code: {ErrorCode}.
                Exception type: {ExceptionType}.
                HTTP status: {StatusCode}.
                Retryable: {Retryable}.
                """,
                failure.ErrorCode,
                exception.GetType().FullName,
                failure.StatusCode,
                failure.Retryable);

            await FailAsync(
                job,
                failure.ErrorCode,
                failure.Retryable,
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
                """
                Image description {DescriptionId} could not complete
                because its worker lease was no longer active.
                """,
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

            nextRetryAt = _timeProvider
                .GetUtcNow()
                .UtcDateTime
                .AddSeconds(delaySeconds);
        }

        var recorded = await _repository.RecordFailureAsync(
            job,
            errorCode,
            nextRetryAt,
            cancellationToken);

        if (!recorded)
        {
            _logger.LogWarning(
                "Could not record failure for description {DescriptionId}.",
                job.Id);

            return;
        }

        _logger.LogWarning(
            """
            Image description {DescriptionId} attempt {Attempt} failed.
            Code: {ErrorCode}.
            Retry scheduled: {RetryScheduled}.
            """,
            job.Id,
            job.Attempts,
            errorCode,
            nextRetryAt.HasValue);
    }

    private Task DelayAsync(
        CancellationToken cancellationToken)
    {
        return Task.Delay(
            TimeSpan.FromSeconds(
                _settings.PollIntervalSeconds),
            cancellationToken);
    }

    private static GeminiFailure DescribeGeminiFailure(
        Exception exception)
    {
        var statusCode = ReadHttpStatusCode(exception);
        var exceptionType = exception.GetType().Name;

        var errorCode = statusCode switch
        {
            400 => "AI_BAD_REQUEST",
            401 => "AI_UNAUTHORIZED",
            403 => "AI_FORBIDDEN",
            404 => "AI_MODEL_OR_ENDPOINT_NOT_FOUND",
            408 => "AI_REQUEST_TIMEOUT",
            429 => "AI_RATE_LIMITED",
            >= 500 => "AI_PROVIDER_SERVER_ERROR",
            _ when string.Equals(
                exceptionType,
                "ServerError",
                StringComparison.Ordinal) =>
                "AI_PROVIDER_SERVER_ERROR",
            _ when string.Equals(
                exceptionType,
                "ClientError",
                StringComparison.Ordinal) =>
                "AI_CLIENT_ERROR",
            _ => "AI_REQUEST_FAILED"
        };

        var retryable = statusCode switch
        {
            400 or 401 or 403 or 404 => false,
            408 or 429 => true,
            >= 500 => true,
            _ when string.Equals(
                exceptionType,
                "ServerError",
                StringComparison.Ordinal) => true,
            _ => true
        };

        return new GeminiFailure(
            errorCode,
            statusCode,
            retryable);
    }

    private static int? ReadHttpStatusCode(Exception exception)
    {
        var rawStatus = ReadPublicProperty(
            exception,
            "StatusCode") ??
            ReadPublicProperty(exception, "Status");

        return rawStatus switch
        {
            int status => status,
            HttpStatusCode status => (int)status,
            _ when int.TryParse(
                rawStatus?.ToString(),
                out var status) => status,
            _ => null
        };
    }

    private static object? ReadPublicProperty(
        object instance,
        string propertyName)
    {
        foreach (var property in instance.GetType().GetProperties(
                     BindingFlags.Instance |
                     BindingFlags.Public))
        {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal) ||
                property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                var value = property.GetValue(instance);

                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // Diagnostic reflection must never interrupt retries.
            }
        }

        return null;
    }

    private sealed record GeminiFailure(
        string ErrorCode,
        int? StatusCode,
        bool Retryable);
}
