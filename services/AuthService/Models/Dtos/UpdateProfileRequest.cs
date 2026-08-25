using System.ComponentModel.DataAnnotations;

namespace AuthService.Models.Dtos;

public class UpdateProfileRequest
{
    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\+[1-9]\d{6,14}$",
        ErrorMessage = "phone_no must be in E.164 format with a leading '+' (e.g. +94771234567).")]
    public string PhoneNo { get; set; } = string.Empty;
}
