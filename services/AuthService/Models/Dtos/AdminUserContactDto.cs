namespace AuthService.Models.Dtos;

public record AdminUserContactDto
{
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string PhoneNo { get; init; } = string.Empty;
}
