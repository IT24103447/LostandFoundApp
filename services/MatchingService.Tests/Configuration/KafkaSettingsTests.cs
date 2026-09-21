using MatchingService.Configuration;

namespace MatchingService.Tests.Configuration;

/// <summary>
/// Story 1, Scenarios 2 and 10 contract tests for the Kafka topic names Matching Service subscribes to.
/// Item Service publishes to "{TopicPrefix}.lost_item.created", ".found_item.created", ".lost_item.updated"
/// and ".found_item.updated". A mismatch here would silently stop events arriving, so the exact names
/// are pinned.
/// </summary>
public sealed class KafkaSettingsTests
{
    // The four topic names, built from the default prefix, match what Item Service publishes to.
    [Fact]
    public void Topics_DefaultPrefix_MatchItemServiceTopicNames()
    {
        var settings = new KafkaSettings();

        Assert.Equal("items.lost_item.created", settings.LostItemCreatedTopic);
        Assert.Equal("items.found_item.created", settings.FoundItemCreatedTopic);
        Assert.Equal("items.lost_item.updated", settings.LostItemUpdatedTopic);
        Assert.Equal("items.found_item.updated", settings.FoundItemUpdatedTopic);
    }

    // A configured prefix applies to all four topics, including the updated ones.
    [Fact]
    public void Topics_CustomPrefix_AppliesToAllFourTopics()
    {
        var settings = new KafkaSettings { TopicPrefix = "staging" };

        Assert.Equal("staging.lost_item.created", settings.LostItemCreatedTopic);
        Assert.Equal("staging.found_item.created", settings.FoundItemCreatedTopic);
        Assert.Equal("staging.lost_item.updated", settings.LostItemUpdatedTopic);
        Assert.Equal("staging.found_item.updated", settings.FoundItemUpdatedTopic);
    }
}
