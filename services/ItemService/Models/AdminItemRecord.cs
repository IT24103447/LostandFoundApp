namespace ItemService.Models;

public sealed class AdminItemRecord
{
    public Guid Id { get; init; }
    public AdminItemType Type { get; init; }
    public Guid UserId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? DeletedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;
}
