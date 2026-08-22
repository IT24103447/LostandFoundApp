namespace AuthService.Configuration;

public class AuthSettings
{
    public int OtpExpiryMinutes { get; set; } = 10;
    public int MaxOtpAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int VerificationSessionMinutes { get; set; } = 30;
    public string FrontendBaseUrl { get; set; } = string.Empty;
}
