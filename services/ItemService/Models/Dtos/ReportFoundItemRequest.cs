using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace ItemService.Models.Dtos;

public class ReportFoundItemRequest
{
    [Required(ErrorMessage = "Title is required.")]
    [MaxLength(150, ErrorMessage = "Title must be at most 150 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required.")]
    [MaxLength(50, ErrorMessage = "Category must be at most 50 characters.")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "Description is required.")]
    [MaxLength(2000, ErrorMessage = "Description must be at most 2000 characters.")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "Date found is required.")]
    public string DateFound { get; set; } = string.Empty;

    [Required(ErrorMessage = "Location found is required.")]
    [MaxLength(255, ErrorMessage = "Location found must be at most 255 characters.")]
    public string LocationFound { get; set; } = string.Empty;
    
    // Used internally to help identify the item.
    [Required(ErrorMessage = "Hidden information is required.")]
    [MaxLength(500, ErrorMessage = "Hidden information must be at most 500 characters.")]
    public string HiddenInformation { get; set; } = string.Empty;

    // Optional photos uploaded with the report.
    public List<IFormFile>? Photos { get; set; }
}
