using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MatchingService.Tests.Support;

/// <summary>
/// Mints real, signed HS256 JWTs for the Story 2 claims/matches endpoints, which now require
/// [Authorize(Policy = "VerifiedClaimUser")] (ClaimRegistration.cs). The claim shape mirrors what
/// AuthService's own JwtTokenService actually issues (JwtRegisteredClaimNames.Sub +
/// an "email_verified" claim of "1"/"0"), not an artificial test-only shape, so these tokens exercise
/// the real validation and claims-reading path the same way a production token would.
/// The Secret/Issuer/Audience constants here are the single source of truth: every WebApplicationFactory
/// that hosts Program.cs (ClaimsApiFactory, and also MatchingServiceDbApiFactory/
/// MatchingServiceKafkaApiFactory from Story 1, since AddManualClaims now runs unconditionally for
/// them too) sets Jwt:Secret/Issuer/Audience to these exact values as environment variables. They must
/// all agree: Environment.SetEnvironmentVariable is process-wide, and xUnit runs different test
/// classes' fixtures concurrently by default, so if any of them used a different value, whichever
/// fixture's write won that race could make ClaimsApiFactory validate a real token's signature against
/// the wrong secret.
/// </summary>
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

    private static string CreateToken(
        Guid userId,
        bool emailVerified,
        TimeSpan? expiresIn = null,
        string? secret = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("email_verified", emailVerified ? "1" : "0")
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
