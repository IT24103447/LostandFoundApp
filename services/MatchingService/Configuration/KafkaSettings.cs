namespace MatchingService.Configuration;

public sealed class KafkaSettings
{
    public string BootstrapServers { get; init; } = "localhost:9092";

    public string GroupId { get; init; } =
        "matching-service-image-descriptions";

    public string TopicPrefix { get; init; } = "items";

    public string LostItemCreatedTopic =>
        $"{TopicPrefix}.lost_item.created";

    public string FoundItemCreatedTopic =>
        $"{TopicPrefix}.found_item.created";

    public string LostItemUpdatedTopic =>
        $"{TopicPrefix}.lost_item.updated";

    public string FoundItemUpdatedTopic =>
        $"{TopicPrefix}.found_item.updated";
}