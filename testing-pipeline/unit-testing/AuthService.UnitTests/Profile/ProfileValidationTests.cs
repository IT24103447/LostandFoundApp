using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;

namespace AuthService.UnitTests.Profile;

public class ProfileValidationTests
{
    [Theory]
    [InlineData("Empty", false)]
    [InlineData("Whitespace", false)]
    [InlineData("MinimumLength", true)]
    [InlineData("MaximumLength", true)]
    [InlineData("TooLong", false)]
    public void Profile_name_validation_checks_required_and_length_boundaries(string scenario, bool valid)
    {
        // Current DTO accepts any nonempty name up to 150 characters.
        var name = scenario switch
        {
            "Empty" => "", "Whitespace" => "   ", "MinimumLength" => "A",
            "MaximumLength" => new string('a', 150), "TooLong" => new string('a', 151),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        AssertValidation(new UpdateProfileRequest { Name = name, PhoneNo = "+94771234567" }, valid, nameof(UpdateProfileRequest.Name));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("0771234567", false)]
    [InlineData("+94", false)]
    [InlineData("+0771234567", false)]
    [InlineData("+94abc4567", false)]
    [InlineData("+1234567890123456", false)]
    [InlineData("+1234567", true)]
    [InlineData("+123456789012345", true)]
    [InlineData("+94771234567", true)]
    public void Profile_phone_validation_checks_e164_format_and_length_boundaries(string phone, bool valid)
    {
        AssertValidation(new UpdateProfileRequest { Name = "Profile User", PhoneNo = phone }, valid, nameof(UpdateProfileRequest.PhoneNo));
    }

    private static void AssertValidation(UpdateProfileRequest request, bool valid, string field)
    {
        // DTO tests exercise MVC's validation rules, not a manually invoked controller.
        var errors = new List<ValidationResult>();
        Assert.Equal(valid, Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        if (valid) Assert.Empty(errors);
        else Assert.Contains(field, Assert.Single(errors).MemberNames);
    }
}
