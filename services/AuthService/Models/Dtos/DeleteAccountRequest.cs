using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class DeleteAccountRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;
}
