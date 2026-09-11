namespace ItemService.Models;

public class ItemSearchQuery
{
    public string? Keyword { get; set; }
    public string? Category { get; set; }
    public string? ItemType { get; set; } // "LOST", "FOUND", or null/omitted = both
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}