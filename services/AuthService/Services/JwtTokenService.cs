using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AuthService.Configuration;
using AuthService.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Services;

public interface IJwtTokenService
{
    string IssueLoginToken(User user);
    string IssueSessionToken(Guid userId);
}

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _jwt;
    private readonly AuthSettings _auth;
    private readonly byte[] _key;

    public JwtTokenService(IOptions<JwtSettings> jwt, IOptions<AuthSettings> auth)
    {
        _jwt = jwt.Value;
        _auth = auth.Value;
        _key = Encoding.UTF8.GetBytes(_jwt.Secret);
    }

    public string IssueLoginToken(User user)
    {
        var expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("email", user.Email),
            new Claim("name", user.Name),
            new Claim("phone_no", user.PhoneNo),
            new Claim("is_admin", user.IsAdmin ? "1" : "0"),
            new Claim("email_verified", user.IsEmailVerified ? "1" : "0"),
            new Claim("created_at", user.CreatedAt.ToUniversalTime().ToString("o"))
        };

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string IssueSessionToken(Guid userId)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
        };
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_auth.VerificationSessionMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
