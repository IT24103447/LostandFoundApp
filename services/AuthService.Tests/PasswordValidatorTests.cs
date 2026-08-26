using AuthService.Services;
using Xunit;

namespace AuthService.Tests;

// Covers Acceptance Criteria Scenario 3 – Weak Password
public class PasswordValidatorTests
{
    private readonly PasswordValidator _sut = new();

    [Fact]
    public void Validate_TooShortPassword_ReturnsError()
    {
        var (isValid, errors) = _sut.Validate("Ab1");

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("at least 8 characters"));
    }

    [Fact]
    public void Validate_NoUppercase_ReturnsError()
    {
        var (isValid, errors) = _sut.Validate("lowercase1");

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("uppercase"));
    }

    [Fact]
    public void Validate_NoLowercase_ReturnsError()
    {
        var (isValid, errors) = _sut.Validate("UPPERCASE1");

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("lowercase"));
    }

    [Fact]
    public void Validate_NoDigit_ReturnsError()
    {
        var (isValid, errors) = _sut.Validate("NoDigitsHere");

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("digit"));
    }

    [Fact]
    public void Validate_TooLongPassword_ReturnsError()
    {
        var tooLong = "Aa1" + new string('x', 130); // > 128 chars

        var (isValid, errors) = _sut.Validate(tooLong);

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("at most 128 characters"));
    }

    [Theory]
    [InlineData("Str0ngPass1")]
    [InlineData("Another9Valid")]
    [InlineData("MinLen8a1")]
    public void Validate_StrongPassword_Passes(string password)
    {
        var (isValid, errors) = _sut.Validate(password);

        Assert.True(isValid);
        Assert.Empty(errors);
    }
}
