using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MatchingService.Tests.Support;

/// <summary>Mints real, signed HS256 JWTs for the Story 2 claims/matches endpoints, matching AuthService's own claim shape. Secret/Issuer/Audience here are the single source of truth every WebApplicationFactory-based fixture must agree on — Environment.SetEnvironmentVariable is process-wide, and xUnit runs fixtures concurrently, so a mismatched value would race.</summary>
public static class JwtTestTokenFactory
{
    public const string Secret = "matching-service-claims-tests-secret-key-32-bytes-minimum";
    public const string Issuer = "matching-service-claims-tests";
    public const string Audience = "matching-service-claims-tests";

    /// <summary>
    /// A verified user's token: carries "sub" = userId and "email_verified" = "1", exactly what the
    /// VerifiedClaimUser policy requires and what ClaimsController/MatchQueriesController read.
    /// </summary>
    public static string CreateVerifiedUserToken(Guid userId) =>
        CreateToken(userId, emailVerified: true);

    // A real user whose email is not yet verified: authenticates fine, but fails the policy's RequireClaim check.
    public static string CreateUnverifiedUserToken(Guid userId) =>
        CreateToken(userId, emailVerified: false);

    // Signed with the wrong key: fails signature validation entirely (not just the policy).
    public static string CreateTokenWithWrongSecret(Guid userId) =>
        CreateToken(userId, emailVerified: true, secret: "a-completely-different-secret-key-32-bytes-min");

    // Already expired: fails ValidateLifetime.
    public static string CreateExpiredToken(Guid userId) =>
        CreateToken(userId, emailVerified: true, expiresIn: TimeSpan.FromMinutes(-5));

    /// <summary>
    /// A real admin's token: carries "is_admin" = "1", which is exactly what the AdminOnly policy requires
    /// (RequireClaim("is_admin", "1")) for the Story LF-338 admin endpoints. Without it every admin route can
    /// only ever be observed returning 403, so the admin happy paths are unreachable over HTTP.
    /// </summary>
    public static string CreateAdminToken(Guid adminId) =>
        CreateToken(adminId, emailVerified: true, isAdmin: true);

    private static string CreateToken(
        Guid userId,
        bool emailVerified,
        TimeSpan? expiresIn = null,
        string? secret = null,
        bool isAdmin = false)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("email_verified", emailVerified ? "1" : "0"),
            new("is_admin", isAdmin ? "1" : "0"),
            new("email", $"{userId}@example.com"),
            new("phone_no", "+94771234567")
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(secret ?? Secret));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(30)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
