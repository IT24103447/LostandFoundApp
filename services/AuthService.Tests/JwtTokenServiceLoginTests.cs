using System.IdentityModel.Tokens.Jwt;
using AuthService.Configuration;
using AuthService.Models;
using AuthService.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthService.Tests.Services;

// These use the REAL JwtTokenService (no mocking) to prove the tokens issued at login
// actually satisfy the story's requirements:
//   - "issues a signed JWT (with user ID, role, expiry)"                (Scenario 1)
//   - "the JWT includes the admin role/claim" for admin accounts        (Scenario 2)
public class JwtTokenServiceLoginTests
{
    private static JwtTokenService BuildService(int expiryMinutes = 60) =>
        new(
            Options.Create(new JwtSettings
            {
                Secret = "unit-test-signing-secret-please-ignore-1234567890",
                Issuer = "lostandfound-auth",
                Audience = "lostandfound-clients",
                ExpiryMinutes = expiryMinutes
            }),
            Options.Create(new AuthSettings()));

    private static User SampleUser(bool isAdmin) => new()
    {
        Id = Guid.NewGuid(),
        Email = "person@example.com",
        PasswordHash = "irrelevant-for-this-test",
        Name = "Person",
        PhoneNo = "+94770000000",
        IsAdmin = isAdmin,
        IsEmailVerified = true,
        IsKicked = false,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public void IssueLoginToken_RegularUser_ContainsUserIdAndExpiry_AndAdminClaimIsFalse()
    {
        var user = SampleUser(isAdmin: false);
        var service = BuildService(expiryMinutes: 45);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(service.IssueLoginToken(user));

        Assert.Equal(user.Id.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("0", jwt.Claims.First(c => c.Type == "is_admin").Value);
        Assert.True(jwt.ValidTo > DateTime.UtcNow); // has a real, future expiry
        Assert.True(jwt.ValidTo <= DateTime.UtcNow.AddMinutes(46)); // roughly matches configured expiry
    }

    [Fact]
    public void IssueLoginToken_AdminUser_ContainsAdminClaimSetToTrue()
    {
        var admin = SampleUser(isAdmin: true);
        var service = BuildService();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(service.IssueLoginToken(admin));

        Assert.Equal(admin.Id.ToString(), jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("1", jwt.Claims.First(c => c.Type == "is_admin").Value);
    }
}
