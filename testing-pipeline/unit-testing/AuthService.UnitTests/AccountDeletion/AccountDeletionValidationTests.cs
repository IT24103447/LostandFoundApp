using System.ComponentModel.DataAnnotations;
using AuthService.Models.Dtos;

namespace AuthService.UnitTests.AccountDeletion;

public class AccountDeletionValidationTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("CurrentPassword123", true)]
    public void Deletion_confirmation_requires_nonempty_password(string? password, bool valid)
    {
        // Validate the DTO separately: MVC's automatic validation is not run by a direct controller call.
        var request = new DeleteAccountRequest { Password = password! };
        var errors = new List<ValidationResult>();

        Assert.Equal(valid, Validator.TryValidateObject(request, new ValidationContext(request), errors, true));
        if (valid) Assert.Empty(errors);
        else Assert.Contains(nameof(DeleteAccountRequest.Password), Assert.Single(errors).MemberNames);
    }
}
