namespace ItemService.Models;

public class FoundItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly DateFound { get; set; }
    public string LocationFound { get; set; } = string.Empty;

    public string HiddenInformation { get; set; } = string.Empty;

    public FoundItemStatus Status { get; set; } = FoundItemStatus.ACTIVE;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<FoundItemPhoto> Photos { get; set; } = [];
}
