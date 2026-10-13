using System.IdentityModel.Tokens.Jwt;
using System.Text;
using AuthService.Configuration;
using AuthService.Services;
using AuthService.UnitTests.Support;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.UnitTests.Login;

public class LoginJwtTests
{
    // Local test key, never an application secret.
    private static JwtSettings Settings() => new()
    {
        Secret = "unit-testing-only-signing-key-with-at-least-32-bytes",
        Issuer = "unit-test-issuer", Audience = "unit-test-audience", ExpiryMinutes = 30
    };

    private static TokenValidationParameters Validation(JwtSettings settings, string? key = null) => new()
    {
        ValidateIssuer = true, ValidIssuer = settings.Issuer,
        ValidateAudience = true, ValidAudience = settings.Audience,
        ValidateIssuerSigningKey = true, RequireSignedTokens = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? settings.Secret)),
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Signed_login_jwt_has_correct_identity_role_verification_and_expiry(bool admin, bool verified)
    {
        var settings = Settings();
        var user = AuthControllerTestContext.User(verified);
        user.IsAdmin = admin;
        var service = new JwtTokenService(Options.Create(settings), Options.Create(new AuthSettings()));
        var earliestExpiry = DateTime.UtcNow.AddMinutes(settings.ExpiryMinutes).AddSeconds(-1);

        var encoded = service.IssueLoginToken(user);

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(encoded, Validation(settings), out var validated);
        var token = Assert.IsType<JwtSecurityToken>(validated);
        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Header.Alg);
        Assert.Equal(user.Id.ToString(), principal.FindFirst("sub")?.Value);
        Assert.Equal(user.Email, principal.FindFirst("email")?.Value);
        Assert.Equal(user.Name, principal.FindFirst("name")?.Value);
        Assert.Equal(user.PhoneNo, principal.FindFirst("phone_no")?.Value);
        Assert.Equal(admin ? "1" : "0", principal.FindFirst("is_admin")?.Value);
        Assert.Equal(verified ? "1" : "0", principal.FindFirst("email_verified")?.Value);
        Assert.InRange(token.ValidTo, earliestExpiry, DateTime.UtcNow.AddMinutes(settings.ExpiryMinutes));
    }

    [Fact]
    public void Login_jwt_is_rejected_when_validated_with_wrong_signing_key()
    {
        var settings = Settings();
        var service = new JwtTokenService(Options.Create(settings), Options.Create(new AuthSettings()));
        var encoded = service.IssueLoginToken(AuthControllerTestContext.User());

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(encoded,
            Validation(settings, "another-unit-test-signing-key-at-least-32-bytes"), out _));
    }
}
