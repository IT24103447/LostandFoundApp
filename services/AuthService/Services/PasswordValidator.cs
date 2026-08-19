namespace AuthService.Services;

/// <summary>
/// Stateless password complexity validator.
/// Enforces: length 8..128, at least one uppercase, one lowercase, one digit.
/// </summary>
public class PasswordValidator
{
    public (bool IsValid, IReadOnlyList<string> Errors) Validate(string password)
    {
        var errors = new List<string>(capacity: 4);

        if (password.Length < 8)
        {
            errors.Add("Password must be at least 8 characters long.");
        }

        if (password.Length > 128)
        {
            errors.Add("Password must be at most 128 characters long.");
        }

        if (!password.Any(char.IsUpper))
        {
            errors.Add("Password must contain at least one uppercase letter.");
        }

        if (!password.Any(char.IsLower))
        {
            errors.Add("Password must contain at least one lowercase letter.");
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add("Password must contain at least one digit.");
        }

        return (errors.Count == 0, errors);
    }
}
