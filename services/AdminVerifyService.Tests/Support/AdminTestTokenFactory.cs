using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AdminVerifyService.Tests.Support;

/// <summary>
/// Mints real, signed HS256 JWTs for the LF-87 spam-review API tests, matching the AdminVerify
/// service's own JwtBearer validation config (Jwt:Secret/Issuer/Audience, ValidAlgorithms = HS256,
/// symmetric key) and the AdminOnly policy shape (RequireAuthenticatedUser + RequireClaim("is_admin","1")).
///
/// These constants are the single source of truth every WebApplicationFactory-based fixture in this
/// test project must agree on: Program.cs reads Jwt:* from environment variables (see
/// AdminVerifyApiFactory.SetPreBuildEnvironmentVariables), and Environment.SetEnvironmentVariable is
/// process-wide, so a mismatched value anywhere would break signature checks elsewhere.
/// </summary>
public static class AdminTestTokenFactory
{
    // 50 ASCII bytes - comfortably above the 32-byte minimum AddAdminSecurity enforces.
    public const string Secret = "admin-verify-service-tests-secret-key-32-bytes-min";

    public const string Issuer = "admin-verify-service-tests";
    public const string Audience = "admin-verify-service-tests";

    /// <summary>A valid admin token: passes the AdminOnly policy (is_admin = "1").</summary>
    public static string CreateAdminToken(Guid userId) =>
        CreateToken(userId, isAdmin: true);

    /// <summary>Authenticates fine but fails the policy's RequireClaim("is_admin", "1") check.</summary>
    public static string CreateNonAdminToken(Guid userId) =>
        CreateToken(userId, isAdmin: false);

    /// <summary>Authenticates fine but carries no is_admin claim at all - 403 for a different reason.</summary>
    public static string CreateTokenWithoutAdminClaim(Guid userId) =>
        CreateToken(userId, isAdmin: false, omitAdminClaim: true);

    /// <summary>Already expired: fails ValidateLifetime in the JwtBearer handler (401, not 403).</summary>
    public static string CreateExpiredToken(Guid userId) =>
        CreateToken(userId, expiresIn: TimeSpan.FromMinutes(-5));

    /// <summary>Signed with a different key: fails signature validation (401, not 403).</summary>
    public static string CreateTokenWithWrongSecret(Guid userId) =>
        CreateToken(userId, secret: "a-completely-different-secret-key-32-bytes-min");

    private static string CreateToken(
        Guid userId,
        bool isAdmin = false,
        bool omitAdminClaim = false,
        TimeSpan? expiresIn = null,
        string? secret = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString())
        };

        if (!omitAdminClaim)
        {
            claims.Add(new Claim("is_admin", isAdmin ? "1" : "0"));
        }

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