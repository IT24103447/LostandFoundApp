using MatchingService.Configuration;
using MatchingService.Models;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace MatchingService.Tests.Services;

/// <summary>
/// Story 1, Scenarios 3/6/7 contract tests for the background Pending -> Completed/Failed worker.
/// ImageDescriptionWorker is a sealed BackgroundService with no public single-cycle method (unlike
/// ItemService's OutboxRelayService, which exposes ProcessBatchAsync for exactly this reason). It's
/// driven here through IHostedService.StartAsync/StopAsync directly. That's the supported, documented
/// way to exercise a BackgroundService's ExecuteAsync loop from a test without subclassing or reflection.
/// Each test lets the worker claim and process exactly one job (the repository mock returns a job on
/// the first ClaimNextAsync call and null afterwards), waits for the outcome via a
/// TaskCompletionSource signalled from inside the relevant repository call, then stops the worker.
/// </summary>
public sealed class ImageDescriptionWorkerTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid LeaseToken = Guid.NewGuid();
    private static readonly Guid ItemId = Guid.NewGuid();
    private static readonly DateTime SourceOccurredAt = DateTime.UtcNow;
    private const string BlobUrl = "https://blob.example.com/a.jpg";

    private static IHostedService BuildWorker(
        Mock<IImageDescriptionRepository> repository,
        Mock<IImageDescriptionGenerator> generator,
        ImageProcessingSettings settings) =>
        new ImageDescriptionWorker(
            repository.Object,
            generator.Object,
            Options.Create(settings),
            Options.Create(new GeminiSettings { ApiKey = "test", Model = "test-model" }),
            TimeProvider.System,
            Mock.Of<ILogger<ImageDescriptionWorker>>());

    private static ImageProcessingSettings FastPollSettings(
        int requestTimeoutSeconds = 90, int maxAttempts = 5) => new()
    {
        Enabled = true,
        PollIntervalSeconds = 0,
        RequestTimeoutSeconds = requestTimeoutSeconds,
        LeaseSeconds = 60,
        MaxAttempts = maxAttempts,
        InitialRetrySeconds = 30,
        MaxRetrySeconds = 1800
    };

    private static void ClaimOnceThenIdle(
        Mock<IImageDescriptionRepository> repository, int attempts = 1)
    {
        var claimed = 0;
        repository
            .Setup(r => r.ClaimNextAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
                Interlocked.Increment(ref claimed) == 1
                    ? new ClaimedImageDescription(
                        JobId, LeaseToken, ItemId, ItemType.Lost, ItemEventType.Created, SourceOccurredAt, BlobUrl, attempts)
                    : null);
    }

    /* Scenario 3: a successful Gemini call results in the job being completed with the generated
       description and the configured model name. */
    [Fact]
    public async Task Worker_SuccessfulGeneration_CompletesJobWithDescriptionAndModelName()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        ClaimOnceThenIdle(repository);

        var description = new GeneratedImageDescription { Description = "A blue backpack." };
        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(description);

        var tcs = new TaskCompletionSource();
        ClaimedImageDescription? completedJob = null;
        repository
            .Setup(r => r.CompleteAsync(
                It.IsAny<ClaimedImageDescription>(), description, "test-model", It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, GeneratedImageDescription, string, CancellationToken>(
                (job, _, _, _) => { completedJob = job; tcs.TrySetResult(); })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings());
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobId, completedJob!.Id);
        repository.Verify(
            r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /* Scenario 7: Gemini taking longer than RequestTimeoutSeconds must fail the job as AI_TIMEOUT,
       retryable, rather than hang the worker indefinitely on one slow request. */
    [Fact]
    public async Task Worker_GenerationExceedsRequestTimeout_RecordsFailureAsAiTimeoutRetryable()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        ClaimOnceThenIdle(repository);

        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new GeneratedImageDescription { Description = "unreachable" };
            });

        var tcs = new TaskCompletionSource();
        string? recordedCode = null;
        DateTime? recordedNextRetryAt = null;
        repository
            .Setup(r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, code, nextRetryAt, _) =>
            {
                recordedCode = code;
                recordedNextRetryAt = nextRetryAt;
                tcs.TrySetResult();
            })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings(requestTimeoutSeconds: 1));
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("AI_TIMEOUT", recordedCode);
        Assert.NotNull(recordedNextRetryAt);
    }

    /* Scenario 7: a retryable ImageProcessingException (e.g. an unusable Gemini response) schedules
       a future retry rather than giving up immediately. */
    [Fact]
    public async Task Worker_RetryableImageProcessingException_SchedulesNextRetry()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        ClaimOnceThenIdle(repository, attempts: 1);

        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageProcessingException("AI_OUTPUT_INVALID", retryable: true));

        var tcs = new TaskCompletionSource();
        string? recordedCode = null;
        DateTime? recordedNextRetryAt = null;
        repository
            .Setup(r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, code, nextRetryAt, _) =>
            {
                recordedCode = code;
                recordedNextRetryAt = nextRetryAt;
                tcs.TrySetResult();
            })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings());
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("AI_OUTPUT_INVALID", recordedCode);
        Assert.NotNull(recordedNextRetryAt);
    }

    /* A non-retryable ImageProcessingException (e.g. an unsupported image type) must not schedule a retry at all. 
       nextRetryAt stays null regardless of remaining attempt budget. */
    [Fact]
    public async Task Worker_NonRetryableImageProcessingException_DoesNotScheduleRetry()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        ClaimOnceThenIdle(repository, attempts: 1);

        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageProcessingException("IMAGE_TYPE_UNSUPPORTED", retryable: false));

        var tcs = new TaskCompletionSource();
        string? recordedCode = null;
        DateTime? recordedNextRetryAt = null;
        repository
            .Setup(r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, code, nextRetryAt, _) =>
            {
                recordedCode = code;
                recordedNextRetryAt = nextRetryAt;
                tcs.TrySetResult();
            })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings());
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("IMAGE_TYPE_UNSUPPORTED", recordedCode);
        Assert.Null(recordedNextRetryAt);
    }

    /* Any exception type the worker doesn't explicitly recognise (e.g. a raw network failure from the Gemini client)
       is normalized to AI_REQUEST_FAILED, retryable. It's never left unhandled. */
    [Fact]
    public async Task Worker_UnrecognisedException_RecordsFailureAsAiRequestFailedRetryable()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        ClaimOnceThenIdle(repository, attempts: 1);

        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected client failure"));

        var tcs = new TaskCompletionSource();
        string? recordedCode = null;
        repository
            .Setup(r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, code, _, _) =>
            {
                recordedCode = code;
                tcs.TrySetResult();
            })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings());
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("AI_REQUEST_FAILED", recordedCode);
    }

    /* Once a job has already reached MaxAttempts, a further failure must not schedule another retry
       even though the failure itself is of a retryable kind. The attempt budget, not just the errortype, 
       gates whether a retry is scheduled. */
    [Fact]
    public async Task Worker_RetryableFailureAtMaxAttempts_DoesNotScheduleFurtherRetry()
    {
        var repository = new Mock<IImageDescriptionRepository>();
        var generator = new Mock<IImageDescriptionGenerator>();
        const int maxAttempts = 3;
        ClaimOnceThenIdle(repository, attempts: maxAttempts);

        generator
            .Setup(g => g.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageProcessingException("AI_OUTPUT_INVALID", retryable: true));

        var tcs = new TaskCompletionSource();
        DateTime? recordedNextRetryAt = null;
        repository
            .Setup(r => r.RecordFailureAsync(
                It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, _, nextRetryAt, _) =>
            {
                recordedNextRetryAt = nextRetryAt;
                tcs.TrySetResult();
            })
            .ReturnsAsync(true);

        var worker = BuildWorker(repository, generator, FastPollSettings(maxAttempts: maxAttempts));
        await worker.StartAsync(CancellationToken.None);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.Null(recordedNextRetryAt);
    }
}
