using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class VerifyEmailRequest
{
    [Required]
    public string SessionToken { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Code must be 6 digits.")]
    public string Code { get; set; } = string.Empty;
}
