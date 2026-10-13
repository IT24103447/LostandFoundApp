using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;

namespace AuthService.UnitTests.EmailVerification;

public class EmailVerificationValidationTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("123456", true)]
    [InlineData("000001", true)]
    public void Otp_validation_requires_exactly_six_digits(string code, bool valid)
    {
        var request = new VerifyEmailRequest { SessionToken = "session", Code = code };
        var errors = new List<ValidationResult>();
        // Direct controller calls bypass MVC validation; exercise the real DTO separately.
        var actual = Validator.TryValidateObject(request, new ValidationContext(request), errors, true);

        Assert.Equal(valid, actual);
        if (valid) Assert.Empty(errors);
        else Assert.Contains(nameof(VerifyEmailRequest.Code), Assert.Single(errors).MemberNames);
    }

    [Fact]
    public void Verification_requires_session_token()
    {
        var request = new VerifyEmailRequest { Code = "123456" };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        Assert.Contains(nameof(VerifyEmailRequest.SessionToken), Assert.Single(errors).MemberNames);
    }

    [Theory]
    [InlineData("", "story@example.test", "SessionToken")]
    [InlineData("session", "", "Email")]
    [InlineData("session", "not-an-email", "Email")]
    public void Resend_validation_requires_session_and_valid_email(string session, string email, string field)
    {
        var request = new ResendVerificationRequest { SessionToken = session, Email = email };
        var errors = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        Assert.Contains(field, Assert.Single(errors).MemberNames);
    }
}
