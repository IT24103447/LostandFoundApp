using System.Reflection;
using System.Text.Json;
using MatchingService.Claims;
using MatchingService.Lifecycle;
using MatchingService.Matches;
using Moq;

namespace MatchingService.UnitTests.DeactivateMatches;

public sealed class DeactivateMatchesTests
{
    private const string LostTopic = "items.lost_item.resolved";
    private const string FoundTopic = "items.found_item.resolved";
    private const string DeleteTopic = "items.item.delete_requested";

    [Theory]
    [InlineData(LostTopic, "lost_item.resolved", "LOST", "RESOLVED")]
    [InlineData(FoundTopic, "found_item.resolved", "FOUND", "RESOLVED")]
    [InlineData(DeleteTopic, "item.delete_requested", "FOUND", "DELETED")]
    public void LifecycleEvent_IsMappedToTheCorrectItemAndReason(
        string topic, string eventType, string expectedType, string expectedReason)
    {
        var eventId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
        var json = Payload(eventId, eventType, occurredAt,
            lostItemId: itemId, foundItemId: itemId, itemId: itemId, itemType: " found ");

        var parsed = Parse(topic, json);

        Assert.Equal(eventId, parsed.EventId);
        Assert.Equal(itemId, parsed.ItemId);
        Assert.Equal(expectedType, parsed.ItemType);
        Assert.Equal(expectedReason, parsed.Reason);
        Assert.Equal(occurredAt.UtcDateTime, parsed.OccurredAt);
    }

    [Theory]
    [InlineData(LostTopic, "found_item.resolved")]
    [InlineData(FoundTopic, "lost_item.resolved")]
    [InlineData(DeleteTopic, "lost_item.resolved")]
    public void EventTypeMustMatchItsKafkaTopic(string topic, string eventType)
    {
        var json = Payload(Guid.NewGuid(), eventType, DateTimeOffset.UtcNow,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "LOST");

        Assert.IsType<InvalidDataException>(ParseError(topic, json));
    }

    [Fact]
    public void LifecycleEvent_RequiresTheItemIdentifierForItsTopic()
    {
        var json = Payload(Guid.NewGuid(), "found_item.resolved", DateTimeOffset.UtcNow,
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), "FOUND");

        Assert.IsType<InvalidDataException>(ParseError(FoundTopic, json));
    }

    [Fact]
    public void DeleteEvent_RejectsAnUnknownItemType()
    {
        var json = Payload(Guid.NewGuid(), "item.delete_requested", DateTimeOffset.UtcNow,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "OTHER");

        Assert.IsType<InvalidDataException>(ParseError(DeleteTopic, json));
    }

    [Fact]
    public void DefaultMatchesQuery_RequiresAnActiveMatch()
    {
        var filter = SectionFilter("active");

        Assert.Contains("is_active = 1", filter);
        Assert.DoesNotContain("is_active = 0", filter);
    }

    [Fact]
    public async Task DeactivatedMatchReview_IsReadOnlyAndPreservesTheAuditReasonAndTime()
    {
        var lostReporterId = Guid.NewGuid();
        var match = new StoredMatch(
            Guid.NewGuid(), lostReporterId, Guid.NewGuid(), lostReporterId,
            "LOST_REPORTER_CONFIRMED", false, 75m, DateTime.UtcNow,
            new ClaimItemView(Guid.NewGuid(), "LOST", "Lost item", "Bags", "Blue bag", "2026-10-01", "Library"),
            new ClaimItemView(Guid.NewGuid(), "FOUND", "Found item", "Bags", "Blue bag", "2026-10-02", "Station"))
        {
            DeactivatedAt = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc),
            DeactivationReason = "ITEM_RESOLVED",
            DeactivatedItemType = "LOST"
        };
        var repository = new Mock<IMatchReadRepository>(MockBehavior.Strict);
        repository.Setup(x => x.GetByIdAsync(match.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var review = await new MatchReadService(repository.Object)
            .GetByIdAsync(match.Id, lostReporterId, CancellationToken.None);

        Assert.Equal("DEACTIVATED", review.Status);
        Assert.Equal("deactivated", review.Section);
        Assert.False(review.IsYourTurn);
        Assert.Equal("ITEM_RESOLVED", review.DeactivationReason);
        Assert.Equal(match.DeactivatedAt, review.DeactivatedAt);
    }

    private static ItemLifecycleEvent Parse(string topic, string json) =>
        (ItemLifecycleEvent)Parser.Invoke(null, [topic, json, LostTopic, FoundTopic, DeleteTopic])!;

    private static Exception ParseError(string topic, string json) =>
        Assert.Throws<TargetInvocationException>(() =>
        {
            Parser.Invoke(null, [topic, json, LostTopic, FoundTopic, DeleteTopic]);
        }).InnerException!;

    private static string SectionFilter(string section) =>
        (string)typeof(MatchReadRepository)
            .GetMethod("GetSectionFilter", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [section])!;

    private static readonly MethodInfo Parser = typeof(ItemLifecycleConsumer)
        .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Payload(
        Guid eventId, string eventType, DateTimeOffset timestamp,
        Guid lostItemId, Guid foundItemId, Guid itemId, string itemType) =>
        JsonSerializer.Serialize(new
        {
            eventId, eventType, timestamp, lostItemId, foundItemId, itemId, itemType
        });
}
