namespace ItemService.Models;

public class LostItem
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly DateLost { get; set; }
    public string LastKnownLocation { get; set; } = string.Empty;

    public string HiddenInformation { get; set; } = string.Empty;

    public LostItemStatus Status { get; set; } = LostItemStatus.ACTIVE;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<LostItemPhoto> Photos { get; set; } = [];
}
