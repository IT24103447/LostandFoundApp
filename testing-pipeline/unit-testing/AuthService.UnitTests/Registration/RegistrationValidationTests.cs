using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;

namespace AuthService.UnitTests.Registration;

public class RegistrationValidationTests
{
    private static RegisterRequest Request() => new()
    {
        Email = "registration@example.test", Password = "Strong123",
        Name = "Registration User", PhoneNo = "+94771234567"
    };

    [Theory]
    [InlineData("0771234567")]
    [InlineData("+94")]
    [InlineData("+0771234567")]
    public void Invalid_phone_formats_produce_phone_validation_error(string phone)
    {
        var request = Request();
        request.PhoneNo = phone;

        var errors = Validate(request);

        var error = Assert.Single(errors);
        Assert.Contains(nameof(RegisterRequest.PhoneNo), error.MemberNames);
        Assert.Contains("E.164", error.ErrorMessage);
    }

    [Theory]
    [InlineData(nameof(RegisterRequest.Email))]
    [InlineData(nameof(RegisterRequest.Password))]
    [InlineData(nameof(RegisterRequest.Name))]
    [InlineData(nameof(RegisterRequest.PhoneNo))]
    public void Each_required_field_rejects_empty_input(string field)
    {
        var request = Request();
        switch (field)
        {
            case nameof(RegisterRequest.Email): request.Email = ""; break;
            case nameof(RegisterRequest.Password): request.Password = ""; break;
            case nameof(RegisterRequest.Name): request.Name = ""; break;
            case nameof(RegisterRequest.PhoneNo): request.PhoneNo = ""; break;
        }

        var errors = Validate(request);

        var error = Assert.Single(errors);
        Assert.Contains(field, error.MemberNames);
        Assert.Contains("required", error.ErrorMessage);
    }

    private static List<ValidationResult> Validate(RegisterRequest request)
    {
        // Direct controller calls do not run MVC's automatic model validation.
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true);
        return errors;
    }
}
