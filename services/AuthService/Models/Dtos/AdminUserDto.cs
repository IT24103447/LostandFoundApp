namespace AuthService.Models.Dtos;

public record AdminUserDto
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PhoneNo { get; init; } = string.Empty;
    public bool IsAdmin { get; init; }
    public bool IsEmailVerified { get; init; }
    public bool IsKicked { get; init; }
    public DateTime CreatedAt { get; init; }
}
