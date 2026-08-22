using System.Security.Cryptography;

namespace AuthService.Services;

public class TokenGenerator : ITokenGenerator
{
    public string GenerateCode()
    {
        // RandomNumberGenerator.GetInt32(0, 1_000_000) is unbiased (no modulo bias).
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public string Hash(string code)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(code);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
