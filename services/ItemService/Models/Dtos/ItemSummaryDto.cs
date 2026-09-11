namespace ItemService.Models.Dtos;

public class ItemSummaryDto
{
    public Guid Id { get; set; }
    public string ItemType { get; set; } = string.Empty; // "LOST" or "FOUND"
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;      // date_lost or date_found, yyyy-MM-dd
    public string Location { get; set; } = string.Empty;  // last_known_location or location_found
    public string Status { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}