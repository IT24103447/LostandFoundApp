using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using ItemService.Services;

public class KafkaEventPublisherTests
{
    // UNIT-19 — supported queue-capacity boundary
    [Fact]
    public async Task PublishAsync_UpToOneThousandEvents_CompletesWithoutDropping()
    {
        var logger =
            new Mock<ILogger<KafkaEventPublisher>>();

        var publisher =
            new KafkaEventPublisher(logger.Object);

        // The in-memory publisher is intentionally bounded to 1,000 events.
        // QA only verifies the supported limit; overflow behaviour is out of scope.
        for (int i = 0; i < 1000; i++)
        {
            var exception =
                await Record.ExceptionAsync(async () =>
                    await publisher.PublishAsync(
                        "items.lost_item.created",
                        new { id = i }));

            Assert.Null(exception);
        }

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
}
