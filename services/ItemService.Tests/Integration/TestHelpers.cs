using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

public static class TestAuthHelper
{
    public const string TestJwtSecret =
        "test-secret-key-at-least-32-characters-long!!";

    public const string TestJwtIssuer =
        "auth-service";

    public const string TestJwtAudience =
        "lostandfound-app";

    public static string GenerateValidToken(Guid? userId = null)
    {
        var uid = userId ?? Guid.NewGuid();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, uid.ToString()),
            new Claim(ClaimTypes.NameIdentifier, uid.ToString())
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(TestJwtSecret));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestJwtIssuer,
            audience: TestJwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string GenerateExpiredToken(Guid? userId = null)
    {
        var uid = userId ?? Guid.NewGuid();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, uid.ToString()),
            new Claim(ClaimTypes.NameIdentifier, uid.ToString())
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(TestJwtSecret));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestJwtIssuer,
            audience: TestJwtAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-60),
            expires: DateTime.UtcNow.AddMinutes(-30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static HttpClient CreateClientWithValidCookie(
        ItemServiceApiFactory factory,
        Guid? userId = null)
    {
        var client = factory.CreateClient();

        var token = GenerateValidToken(userId);

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"auth_token={token}");

        return client;
    }

    public static HttpClient CreateClientWithExpiredCookie(
        ItemServiceApiFactory factory)
    {
        var client = factory.CreateClient();

        var token = GenerateExpiredToken();

        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"auth_token={token}");

        return client;
    }
}

public static class TestMultipartHelper
{
    public static MultipartFormDataContent BuildValidFormExcept(
        string? fieldToOmit = null,
        string hiddenInfo = "default-hidden-info")
    {
        var content = new MultipartFormDataContent();

        if (fieldToOmit != "Title")
        {
            content.Add(
                new StringContent("Test Title"),
                "Title");
        }

        if (fieldToOmit != "Category")
        {
            content.Add(
                new StringContent("Electronics"),
                "Category");
        }

        if (fieldToOmit != "Description")
        {
            content.Add(
                new StringContent("Test Description"),
                "Description");
        }

        if (fieldToOmit != "DateLost")
        {
            content.Add(
                new StringContent("2026-08-28"),
                "DateLost");
        }

        if (fieldToOmit != "LastKnownLocation")
        {
            content.Add(
                new StringContent("Test Location"),
                "LastKnownLocation");
        }

        if (fieldToOmit != "HiddenInformation")
        {
            content.Add(
                new StringContent(hiddenInfo),
                "HiddenInformation");
        }

        return content;
    }

    public static MultipartFormDataContent BuildValidForm(
        string hiddenInfo = "default-hidden-info")
    {
        return BuildValidFormExcept(null, hiddenInfo);
    }
}