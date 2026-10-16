namespace ItemService.Models.Dtos;

public sealed class AdminItemDto
{
    public Guid Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? DeletedAt { get; init; }
}
