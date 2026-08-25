using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; set; } = string.Empty;
}
