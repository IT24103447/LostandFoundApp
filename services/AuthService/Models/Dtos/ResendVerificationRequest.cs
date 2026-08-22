using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class ResendVerificationRequest
{
    [Required]
    public string SessionToken { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; set; } = string.Empty;
}
