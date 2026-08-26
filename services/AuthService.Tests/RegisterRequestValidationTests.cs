using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;
using Xunit;

namespace AuthService.Tests;

// Covers Acceptance Criteria Scenario 4 – Invalid Phone Format
//         Acceptance Criteria Scenario 5 – Required Field Validation
// These tests exercise the [Required]/[EmailAddress]/[RegularExpression] attributes
// on RegisterRequest directly, the same way ASP.NET Core's model binding does.
public class RegisterRequestValidationTests
{
    private static RegisterRequest ValidRequest() => new()
    {
        Email = "user@example.com",
        Password = "Str0ngPass1",
        Name = "Test User",
        PhoneNo = "+94771234567"
    };

    private static IList<ValidationResult> Validate(RegisterRequest req)
    {
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(req);
        Validator.TryValidateObject(req, ctx, results, validateAllProperties: true);
        return results;
    }

    [Theory]
    [InlineData("0771234567")]     // missing leading '+'
    [InlineData("+94")]            // too short
    [InlineData("+1234567890123456")] // too long (>15 digits after +)
    [InlineData("phone")]          // not numeric at all
    [InlineData("+0771234567")]    // leading digit after '+' cannot be 0
    public void PhoneNo_InvalidFormats_FailValidation(string invalidPhone)
    {
        var req = ValidRequest();
        req.PhoneNo = invalidPhone;

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.PhoneNo)));
    }

    [Theory]
    [InlineData("+94771234567")]
    [InlineData("+14155552671")]
    public void PhoneNo_ValidE164Format_PassesValidation(string validPhone)
    {
        var req = ValidRequest();
        req.PhoneNo = validPhone;

        var results = Validate(req);

        Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(RegisterRequest.PhoneNo)));
    }

    [Fact]
    public void Email_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.Email = "";

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Email)));
    }

    [Fact]
    public void Email_InvalidFormat_FailsValidation()
    {
        var req = ValidRequest();
        req.Email = "not-an-email";

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Email)));
    }

    [Fact]
    public void Password_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.Password = "";

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Password)));
    }

    [Fact]
    public void Name_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.Name = "";

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.Name)));
    }

    [Fact]
    public void PhoneNo_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.PhoneNo = "";

        var results = Validate(req);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(RegisterRequest.PhoneNo)));
    }

    [Fact]
    public void AllFieldsValid_PassesValidation()
    {
        var req = ValidRequest();

        var results = Validate(req);

        Assert.Empty(results);
    }
}
