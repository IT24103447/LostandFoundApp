namespace AuthService.Services;

public interface IVerificationSessionService
{
    /// <summary>Mints a short-lived JWT identifying an in-progress email verification flow</summary>
    string Issue(Guid userId);

    /// <summary>Returns the user id encoded in the token, or null if invalid / expired</summary>
    Guid? Validate(string token);
}
