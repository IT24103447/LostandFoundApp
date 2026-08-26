using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class ResetPasswordRequest
{
    [Required]
    public string SessionToken { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Code must be exactly 6 digits.")]
    public string Code { get; set; } = string.Empty;

    [Required]
    public string NewPassword { get; set; } = string.Empty;
}
