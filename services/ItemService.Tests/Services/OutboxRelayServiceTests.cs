using Confluent.Kafka;
using ItemService.Configuration;
using ItemService.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

public class OutboxRelayServiceTests
{
    private const string Topic = "items.lost_item.created";

    private readonly Mock<IOutboxStore> _store = new();
    private readonly Mock<IProducer<string, string>> _producer = new();
    private readonly Mock<ILogger<OutboxRelayService>> _logger = new();

    private OutboxRelayService CreateRelay(OutboxSettings? settings = null) =>
        new(_store.Object, _producer.Object, Options.Create(settings ?? new OutboxSettings()), _logger.Object);

    private static OutboxRecord NewRecord(string topic = Topic, string key = "key-1", string payload = "{\"a\":1}", int attempts = 0) =>
        new(Guid.NewGuid(), topic, key, payload, attempts);

    private void SetupClaim(params OutboxRecord[] records) =>
        _store
            .Setup(s => s.ClaimBatchAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(records);

    private void SetupProduceSucceeds() =>
        _producer
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string>());

    private static ProduceException<string, string> BrokerDown() =>
        new(new Error(ErrorCode.Local_MsgTimedOut, "Message timed out"), new DeliveryResult<string, string>());

    private void VerifyLogged(LogLevel level, Times times, string? contains = null) =>
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => contains == null || v.ToString()!.Contains(contains)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    [Fact]
    public async Task ProcessBatch_NothingPending_DoesNotTouchKafka()
    {
        SetupClaim();

        var processed = await CreateRelay().ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        _producer.Verify(p => p.ProduceAsync(
            It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatch_PublishesEventWithStoredTopicKeyAndPayload_ThenMarksItPublished()
    {
        var record = NewRecord(key: "event-id-1", payload: "{\"title\":\"wallet\"}");
        SetupClaim(record);
        SetupProduceSucceeds();

        var processed = await CreateRelay().ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        _producer.Verify(p => p.ProduceAsync(
            Topic,
            It.Is<Message<string, string>>(m => m.Key == "event-id-1" && m.Value == "{\"title\":\"wallet\"}"),
            It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkPublishedAsync(record.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkFailedAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessBatch_BrokerFailure_KeepsEventForRetryWithFutureAttemptTime()
    {
        var record = NewRecord();
        SetupClaim(record);
        _producer
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(BrokerDown());

        var before = DateTime.UtcNow;
        var processed = await CreateRelay().ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        _store.Verify(s => s.MarkPublishedAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.MarkFailedAsync(
            record.Id,
            It.Is<string>(e => e.Contains("timed out")),
            It.Is<DateTime>(next => next > before),
            It.IsAny<CancellationToken>()), Times.Once);
        VerifyLogged(LogLevel.Warning, Times.Once(), "failed to publish");
    }

    [Fact]
    public async Task ProcessBatch_OneFailingEvent_DoesNotBlockTheOthers()
    {
        var bad = NewRecord(topic: "items.bad", key: "bad");
        var good = NewRecord(topic: "items.good", key: "good");
        SetupClaim(bad, good);

        _producer
            .Setup(p => p.ProduceAsync("items.bad", It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(BrokerDown());
        _producer
            .Setup(p => p.ProduceAsync("items.good", It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliveryResult<string, string>());

        await CreateRelay().ProcessBatchAsync(CancellationToken.None);

        _store.Verify(s => s.MarkPublishedAsync(good.Id, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.MarkFailedAsync(
            bad.Id, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessBatch_FinalAllowedAttempt_LogsErrorSoItCanBeAlertedOn()
    {
        var settings = new OutboxSettings { MaxAttempts = 3 };
        SetupClaim(NewRecord(attempts: 2)); // this failure is attempt number 3
        _producer
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(BrokerDown());

        await CreateRelay(settings).ProcessBatchAsync(CancellationToken.None);

        VerifyLogged(LogLevel.Error, Times.Once(), "will not be retried automatically");
    }

    [Fact]
    public async Task ProcessBatch_DeliveredButCannotBeMarked_DoesNotThrowAndWarns()
    {
        var record = NewRecord();
        SetupClaim(record);
        SetupProduceSucceeds();
        _store
            .Setup(s => s.MarkPublishedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var exception = await Record.ExceptionAsync(() => CreateRelay().ProcessBatchAsync(CancellationToken.None));

        Assert.Null(exception);
        VerifyLogged(LogLevel.Warning, Times.Once(), "could not be marked published");
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(20, 300)]
    public void ComputeBackoff_DoublesEachAttemptAndCapsAtFiveMinutes(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxRelayService.ComputeBackoff(attempts));
    }
}
