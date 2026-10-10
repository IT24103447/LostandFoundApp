namespace AdminVerifyService.Configuration;

public sealed class DetectionSettings
{
    public int SpamThreshold { get; init; } = 3;

    public int SpamWindowMinutes { get; init; } = 60;

    // 0 means no cap.
    public int SpamRecordCap { get; init; }
}
