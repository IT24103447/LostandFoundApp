namespace ItemService.Models.Dtos;


public class LostItemResponseDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DateLost { get; set; } = string.Empty;
    public string LastKnownLocation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> PhotoUrls { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
