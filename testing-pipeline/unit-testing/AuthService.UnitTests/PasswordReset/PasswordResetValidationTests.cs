using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;

namespace AuthService.UnitTests.PasswordReset;

public class PasswordResetValidationTests
{
    [Theory]
    [InlineData("Empty", false)]
    [InlineData("Malformed", false)]
    [InlineData("TooLong", false)]
    [InlineData("Valid", true)]
    public void Forgot_password_email_validation_checks_required_format_and_maximum_length(string scenario, bool valid)
    {
        var email = scenario switch
        {
            "Empty" => "", "Malformed" => "not-an-email", "TooLong" => new string('a', 242) + "@example.test",
            _ => "recovery@example.test"
        };
        AssertValidation(new ForgotPasswordRequest { Email = email }, valid, nameof(ForgotPasswordRequest.Email));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("123456", true)]
    [InlineData("000001", true)]
    public void Reset_password_code_validation_requires_exactly_six_digits(string code, bool valid)
    {
        AssertValidation(new ResetPasswordRequest { SessionToken = "session", Code = code, NewPassword = "NewStrong123" }, valid, nameof(ResetPasswordRequest.Code));
    }

    [Theory]
    [InlineData("SessionToken")]
    [InlineData("NewPassword")]
    public void Reset_password_request_requires_session_and_new_password(string field)
    {
        var request = new ResetPasswordRequest { SessionToken = "session", Code = "123456", NewPassword = "NewStrong123" };
        if (field == nameof(ResetPasswordRequest.SessionToken)) request.SessionToken = "";
        else request.NewPassword = "";

        AssertValidation(request, false, field);
    }

    [Fact]
    public void Empty_reset_request_reports_all_required_fields()
    {
        var request = new ResetPasswordRequest();
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        Assert.Equal(new[] { "Code", "NewPassword", "SessionToken" }, errors.SelectMany(e => e.MemberNames).OrderBy(x => x).ToArray());
    }

    private static void AssertValidation(object request, bool valid, string field)
    {
        // DTO validation is separate from direct controller calls, which bypass MVC validation.
        var errors = new List<ValidationResult>();
        Assert.Equal(valid, Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        if (valid) Assert.Empty(errors);
        else Assert.Contains(field, Assert.Single(errors).MemberNames);
    }
}
