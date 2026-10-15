using MatchingService.Configuration;
using MatchingService.Models;
using MatchingService.Repositories;
using MatchingService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace MatchingService.UnitTests.PhotoDescriptionPipeline;

public class PhotoDescriptionWorkerTests
{
    private const string BlobUrl = "https://photos.example.com/items/wallet.jpg";

    [Theory]
    [InlineData("Lost")]
    [InlineData("Found")]
    public async Task CompletedAnalysis_SavesDescriptionAgainstClaimedItemAndPhoto(string type)
    {
        var job = Job(Enum.Parse<ItemType>(type), attempts: 1);
        var description = new GeneratedImageDescription
        {
            Description = "A brown leather wallet with a metal clasp.",
            ObjectType = "wallet", Colours = ["brown"], Materials = ["leather"]
        };
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        var generator = new Mock<IImageDescriptionGenerator>(MockBehavior.Strict);
        ClaimOnce(repository, job);
        generator.Setup(x => x.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(description);
        var completed = new TaskCompletionSource<ClaimedImageDescription>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(x => x.CompleteAsync(job, description, "assessment-model", It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, GeneratedImageDescription, string, CancellationToken>((claimed, _, _, _) =>
                completed.TrySetResult(claimed))
            .ReturnsAsync(true);

        await RunUntilAsync(repository, generator, completed.Task);

        Assert.Equal(job, await completed.Task);
        generator.VerifyAll();
        repository.Verify(x => x.CompleteAsync(job, description, "assessment-model", It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.RecordFailureAsync(It.IsAny<ClaimedImageDescription>(), It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public async Task AnalysisFailure_StoresSafeCodeAndRetriesOnlyWithinAttemptBudget(int attempts, bool retryExpected)
    {
        var job = Job(ItemType.Lost, attempts);
        var repository = new Mock<IImageDescriptionRepository>(MockBehavior.Strict);
        var generator = new Mock<IImageDescriptionGenerator>(MockBehavior.Strict);
        ClaimOnce(repository, job);
        generator.Setup(x => x.GenerateAsync(BlobUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageProcessingException("BLOB_DOWNLOAD_FAILED", retryable: true));
        var failure = new TaskCompletionSource<(string Code, DateTime? RetryAt)>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(x => x.RecordFailureAsync(job, It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimedImageDescription, string, DateTime?, CancellationToken>((_, code, retryAt, _) =>
                failure.TrySetResult((code, retryAt)))
            .ReturnsAsync(true);
        var started = DateTime.UtcNow;

        await RunUntilAsync(repository, generator, failure.Task);

        var recorded = await failure.Task;
        Assert.Equal("BLOB_DOWNLOAD_FAILED", recorded.Code);
        Assert.Equal(retryExpected, recorded.RetryAt.HasValue);
        if (retryExpected)
            Assert.InRange(recorded.RetryAt!.Value, started.AddSeconds(30), DateTime.UtcNow.AddSeconds(30));
        repository.Verify(x => x.CompleteAsync(It.IsAny<ClaimedImageDescription>(), It.IsAny<GeneratedImageDescription>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ClaimedImageDescription Job(ItemType type, int attempts) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, ItemEventType.Created,
        DateTime.UtcNow, BlobUrl, attempts);

    private static void ClaimOnce(Mock<IImageDescriptionRepository> repository, ClaimedImageDescription job)
    {
        var calls = 0;
        repository.Setup(x => x.ClaimNextAsync(3, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref calls) == 1 ? job : null);
    }

    private static async Task RunUntilAsync<T>(
        Mock<IImageDescriptionRepository> repository,
        Mock<IImageDescriptionGenerator> generator,
        Task<T> observation)
    {
        var settings = new ImageProcessingSettings
        {
            Enabled = true,
            PollIntervalSeconds = 1,
            RequestTimeoutSeconds = 5,
            LeaseSeconds = 30,
            MaxAttempts = 3,
            InitialRetrySeconds = 30,
            MaxRetrySeconds = 300
        };
        var worker = new ImageDescriptionWorker(
            repository.Object, generator.Object, Options.Create(settings),
            Options.Create(new GeminiSettings { ApiKey = "test-only", Model = "assessment-model" }),
            TimeProvider.System, NullLogger<ImageDescriptionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await observation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
