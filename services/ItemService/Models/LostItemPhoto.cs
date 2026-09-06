namespace ItemService.Models;

public class LostItemPhoto
{
    public Guid Id { get; set; }
    public Guid LostItemId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
