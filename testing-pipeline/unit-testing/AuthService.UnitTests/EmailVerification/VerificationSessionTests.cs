using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AuthService.Configuration;
using AuthService.Services;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace AuthService.UnitTests.EmailVerification;

public class VerificationSessionTests
{
    [Theory]
    [InlineData("Valid", true)]
    [InlineData("Expired", false)]
    [InlineData("WrongSignature", false)]
    [InlineData("WrongIssuer", false)]
    [InlineData("WrongAudience", false)]
    [InlineData("InvalidSubject", false)]
    [InlineData("Malformed", false)]
    [InlineData("Empty", false)]
    public void Verification_session_validates_signature_identity_and_lifetime(string scenario, bool valid)
    {
        var settings = new JwtSettings
        {
            Secret = "verification-unit-test-signing-key-at-least-32-bytes",
            Issuer = "test-issuer", Audience = "test-audience"
        };
        var id = Guid.NewGuid();
        var key = scenario == "WrongSignature" ? "different-unit-test-signing-key-at-least-32-bytes" : settings.Secret;
        // Expired by ten minutes, beyond the service's two-minute clock skew. No sleep.
        var jwt = new JwtSecurityToken(
            issuer: scenario == "WrongIssuer" ? "other-issuer" : settings.Issuer,
            audience: scenario == "WrongAudience" ? "other-audience" : settings.Audience,
            claims: [new Claim("sub", scenario == "InvalidSubject" ? "invalid-id" : id.ToString())],
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: scenario == "Expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        var token = scenario switch
        {
            "Empty" => "", "Malformed" => "not-a-jwt", _ => new JwtSecurityTokenHandler().WriteToken(jwt)
        };
        var dependency = new Mock<IJwtTokenService>(MockBehavior.Strict);
        var service = new VerificationSessionService(Options.Create(settings), Options.Create(new AuthSettings()), dependency.Object);

        Assert.Equal(valid ? id : (Guid?)null, service.Validate(token));
        dependency.VerifyNoOtherCalls();
    }
}
