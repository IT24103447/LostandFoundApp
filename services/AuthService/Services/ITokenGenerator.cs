namespace AuthService.Services;

public interface ITokenGenerator
{
    /// <summary>Generates a cryptographically random 6-digit code (zero-padded</summary>
    string GenerateCode();

    /// <summary>Returns the SHA-256 hex (lowercase) of the given code</summary>
    string Hash(string code);
}
