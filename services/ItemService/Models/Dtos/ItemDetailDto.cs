namespace ItemService.Models.Dtos;

// Response shape for GET /api/items/{id}
// HiddenInformation is the Matching Service's verification signal and must
// never leak through any frontend-facing DTO.
public class ItemDetailDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;        
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;   
    public string Date { get; set; } = string.Empty;       
    public string Status { get; set; } = string.Empty;
    public List<string> PhotoUrls { get; set; } = [];
    public ItemPhotoDto? Photo { get; set; }
    public DateTime CreatedAt { get; set; }
}