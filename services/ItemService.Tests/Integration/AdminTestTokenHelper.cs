using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using ItemService.Authorization;
using Microsoft.IdentityModel.Tokens;

public static class AdminTestTokenHelper
{
    // Story LF-79 helper: ItemService's AdminOnly policy is claim-based (RequireClaim("is_admin", "1")),
    // so an admin is proved purely by the token. TestAuthHelper deliberately mints no is_admin claim,
    // so this adds only the missing claim and reuses its signing constants to stay in sync.
    public static string GenerateAdminToken(Guid? adminId = null)
    {
        var uid = adminId ?? Guid.NewGuid();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, uid.ToString()),
            new Claim(ClaimTypes.NameIdentifier, uid.ToString()),
            new Claim(AuthorizationPolicies.IsAdminClaim, AuthorizationPolicies.IsAdminValue)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(TestAuthHelper.TestJwtSecret));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestAuthHelper.TestJwtIssuer,
            audience: TestAuthHelper.TestJwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpClient CreateAdminClient(ItemServiceApiFactory factory, Guid? adminId = null)
    {
        var client = factory.CreateClient();

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"auth_token={GenerateAdminToken(adminId)}");

        return client;
    }

    // A verified-but-not-admin caller: authenticates successfully yet fails the AdminOnly claim requirement.
    public static HttpClient CreateNonAdminClient(ItemServiceApiFactory factory, Guid? userId = null) =>
        TestAuthHelper.CreateClientWithValidCookie(factory, userId);
}