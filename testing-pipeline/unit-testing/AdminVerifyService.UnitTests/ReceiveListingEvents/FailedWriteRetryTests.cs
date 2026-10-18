using System.Reflection;
using System.Text.Json;
using AdminVerifyService.Configuration;
using AdminVerifyService.Databases;
using AdminVerifyService.Listings;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AdminVerifyService.UnitTests.ReceiveListingEvents;

public sealed class FailedWriteRetryTests
{
    [Fact]
    public async Task FailedDatabaseWriteRewindsTheMessageAndDoesNotCommitItsOffset()
    {
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        connections.Setup(x => x.Create()).Throws(new InvalidOperationException("Simulated database outage"));
        var handler = new ListingEventHandler(connections.Object, null!, null!, TimeProvider.System);
        var worker = new ListingEventConsumer(handler, Options.Create(new KafkaSettings()),
            NullLogger<ListingEventConsumer>.Instance);
        using var cancellation = new CancellationTokenSource();
        var message = new ConsumeResult<string, string>
        {
            Topic = "items.lost_item.created", Partition = new Partition(0), Offset = new Offset(12),
            Message = new Message<string, string> { Value = JsonSerializer.Serialize(new
            {
                eventId = Guid.NewGuid(), userId = Guid.NewGuid(), lostItemId = Guid.NewGuid(), createdAt = DateTime.UtcNow
            }) }
        };
        var consumer = new Mock<IConsumer<string, string>>(MockBehavior.Strict);
        consumer.Setup(x => x.Consume(cancellation.Token)).Returns(message);
        consumer.Setup(x => x.Seek(message.TopicPartitionOffset)).Callback(() => cancellation.Cancel());
        var loop = typeof(ListingEventConsumer).GetMethod("ConsumeLoopAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (Task)loop.Invoke(worker, [consumer.Object, new[] { message.Topic }, cancellation.Token])!);

        consumer.Verify(x => x.Seek(message.TopicPartitionOffset), Times.Once);
        consumer.Verify(x => x.Commit(It.IsAny<ConsumeResult<string, string>>()), Times.Never);
        connections.Verify(x => x.Create(), Times.Once);
    }
}
