using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public record ConfirmRequest
{
    [Required]
    public bool Confirm { get; init; }
}
