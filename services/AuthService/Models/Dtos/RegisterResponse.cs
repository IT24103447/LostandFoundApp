namespace AuthService.Models.Dtos;

public class RegisterResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsEmailVerified { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string VerificationSessionToken { get; set; } = string.Empty;
}
