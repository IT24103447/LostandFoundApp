using AuthService.Services;

namespace AuthService.UnitTests.Registration;

public class RegistrationPasswordTests
{
    [Theory]
    [InlineData("TooShort", false, "Password must be at least 8 characters long.")]
    [InlineData("TooLong", false, "Password must be at most 128 characters long.")]
    [InlineData("NoUppercase", false, "Password must contain at least one uppercase letter.")]
    [InlineData("NoLowercase", false, "Password must contain at least one lowercase letter.")]
    [InlineData("NoDigit", false, "Password must contain at least one digit.")]
    [InlineData("MinimumLength", true, null)]
    [InlineData("MaximumLength", true, null)]
    public void Password_rules_reject_invalid_inputs_and_accept_length_boundaries(
        string scenario, bool expectedValid, string? expectedError)
    {
        // Short scenario labels keep parameterized console/report output readable.
        var password = scenario switch
        {
            "TooShort" => "Aa1aaaa",
            "TooLong" => "Aa1" + new string('a', 126),
            "NoUppercase" => "abcdefg1",
            "NoLowercase" => "ABCDEFG1",
            "NoDigit" => "Abcdefgh",
            "MinimumLength" => "Aa1aaaaa",
            "MaximumLength" => "Aa1" + new string('a', 125),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var (valid, errors) = new PasswordValidator().Validate(password);

        Assert.Equal(expectedValid, valid);
        if (expectedValid) Assert.Empty(errors);
        else Assert.Equal(new[] { expectedError! }, errors);
    }
}
