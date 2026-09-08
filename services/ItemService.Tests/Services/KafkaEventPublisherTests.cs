using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using ItemService.Services;

public class KafkaEventPublisherTests
{
    // UNIT-19 / BUG-02 regression test
    [Fact]
    public async Task PublishAsync_ChannelFull_DropsSilently_NoWarningEverLogged()
    {
        var logger =
            new Mock<ILogger<KafkaEventPublisher>>();

        var publisher =
            new KafkaEventPublisher(logger.Object);

        for (int i = 0; i < 1050; i++)
        {
            var exception =
                await Record.ExceptionAsync(async () =>
                    await publisher.PublishAsync(
                        "items.lost_item.created",
                        new { id = i }));

            Assert.Null(exception);
        }

        // Current production behavior:
        // DropWrite silently discards events when the channel
        // is full, while TryWrite still reports success.

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