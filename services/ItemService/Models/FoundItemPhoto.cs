namespace ItemService.Models;

public class FoundItemPhoto
{
    public Guid Id { get; set; }
    public Guid FoundItemId { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
