namespace ItemService.Models.Dtos;

// HiddenInformation is excluded from all frontend-facing DTOs.
// Stored only in the domain, DB, and Kafka events.
public class FoundItemResponseDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DateFound { get; set; } = string.Empty;
    public string LocationFound { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> PhotoUrls { get; set; } = [];
    public ItemPhotoDto? Photo { get; set; }
    public DateTime CreatedAt { get; set; }
}
