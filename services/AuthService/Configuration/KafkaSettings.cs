namespace AuthService.Configuration;

public class KafkaSettings
{
    public string BootstrapServers { get; set; } = string.Empty;
    public string TopicPrefix { get; set; } = "auth";
}
