using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthService.UnitTests.Login;

public class LoginControllerTests
{
    private readonly AuthControllerTestContext _context = new();
    private static LoginRequest Request(string email = "story@example.test") => new() { Email = email, Password = "Strong123" };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Valid_credentials_return_user_or_admin_details_and_auth_cookie(bool admin, bool production)
    {
        var user = AuthControllerTestContext.User();
        user.IsAdmin = admin;
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, ct)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify("Strong123", user.PasswordHash)).Returns(true);
        _context.Jwt.Setup(x => x.IssueLoginToken(user)).Returns("signed.jwt");
        var controller = _context.Controller(production);

        var result = await controller.Login(Request(), ct);

        var response = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(user.Id, response.Id);
        Assert.Equal(user.Email, response.Email);
        Assert.Equal(user.Name, response.Name);
        Assert.Equal(user.PhoneNo, response.PhoneNo);
        Assert.Equal(user.CreatedAt, response.CreatedAt);
        Assert.Equal(admin, response.IsAdmin);
        Assert.True(response.IsEmailVerified);
        Assert.Equal("signed.jwt", response.Token);
        var cookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains("auth_token=signed.jwt", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Contains("expires=", cookie);
        Assert.Contains(production ? "samesite=none" : "samesite=lax", cookie);
        Assert.Equal(production, cookie.Contains("; secure"));
        _context.Hasher.Verify(x => x.Verify("Strong123", user.PasswordHash), Times.Once);
        _context.Jwt.Verify(x => x.IssueLoginToken(user), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Theory]
    [InlineData("UnknownEmail")]
    [InlineData("WrongPassword")]
    [InlineData("DeletedAccount")]
    public async Task Invalid_credentials_or_filtered_deleted_account_return_same_error_without_token(string scenario)
    {
        var user = AuthControllerTestContext.User();
        // The real repository excludes soft-deleted rows. Its null result is stubbed here.
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None))
            .ReturnsAsync(scenario == "WrongPassword" ? user : null);
        if (scenario == "WrongPassword")
            _context.Hasher.Setup(x => x.Verify("Strong123", user.PasswordHash)).Returns(false);
        var controller = _context.Controller();

        var result = await controller.Login(Request(), CancellationToken.None);

        var response = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("Invalid email or password.", AuthControllerTestContext.Body(response).GetProperty("error").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Unverified_login_returns_forbidden_and_sends_new_verification_code_without_login_jwt()
    {
        var user = AuthControllerTestContext.User(verified: false);
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify("Strong123", user.PasswordHash)).Returns(true);
        _context.Session.Setup(x => x.Issue(user.Id)).Returns("verification-session");
        _context.Tokens.Setup(x => x.InvalidateAllForUserAsync(user.Id, CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Otp.Setup(x => x.GenerateCode()).Returns("123456");
        _context.Otp.Setup(x => x.Hash("123456")).Returns("otp-hash");
        var earliestExpiry = DateTime.UtcNow.AddMinutes(10);
        _context.Tokens.Setup(x => x.CreateAsync(user.Id, "otp-hash", null, It.IsAny<DateTime>(), CancellationToken.None)).Returns(Task.CompletedTask);
        _context.Email.Setup(x => x.SendAsync(user.Email, "Your Lost & Found verification code",
            It.Is<string>(s => s.Contains("123456")), It.Is<string?>(s => s != null && s.Contains("123456")), CancellationToken.None))
            .Returns(Task.CompletedTask);
        _context.Users.Setup(x => x.SetLastResentAtAsync(user.Id, It.IsAny<DateTime>(), CancellationToken.None)).Returns(Task.CompletedTask);
        var controller = _context.Controller();

        var result = Assert.IsType<ObjectResult>((await controller.Login(Request(), CancellationToken.None)).Result);

        Assert.Equal(403, result.StatusCode);
        var body = AuthControllerTestContext.Body(result);
        Assert.Equal("Email not verified. Please verify your email before signing in.", body.GetProperty("error").GetString());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.Equal("verification-session", body.GetProperty("verificationSessionToken").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.Tokens.Verify(x => x.InvalidateAllForUserAsync(user.Id, CancellationToken.None), Times.Once);
        _context.Tokens.Verify(x => x.CreateAsync(user.Id, "otp-hash", null,
            It.Is<DateTime>(t => t >= earliestExpiry && t <= DateTime.UtcNow.AddMinutes(10)), CancellationToken.None), Times.Once);
        _context.Email.Verify(x => x.SendAsync(user.Email, "Your Lost & Found verification code",
            It.IsAny<string>(), It.IsAny<string?>(), CancellationToken.None), Times.Once);
        _context.AssertAllCallsVerified();
    }

    [Fact]
    public async Task Suspended_verified_account_rejects_login_without_token()
    {
        var user = AuthControllerTestContext.User();
        user.IsKicked = true;
        _context.Users.Setup(x => x.GetByEmailAsync(user.Email, CancellationToken.None)).ReturnsAsync(user);
        _context.Hasher.Setup(x => x.Verify("Strong123", user.PasswordHash)).Returns(true);
        var controller = _context.Controller();

        var result = Assert.IsType<ObjectResult>((await controller.Login(Request(), CancellationToken.None)).Result);

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Your account has been suspended.", AuthControllerTestContext.Body(result).GetProperty("error").GetString());
        Assert.False(controller.Response.Headers.ContainsKey("Set-Cookie"));
        _context.AssertAllCallsVerified();
    }
}
