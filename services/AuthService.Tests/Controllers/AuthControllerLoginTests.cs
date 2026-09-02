using System.IdentityModel.Tokens.Jwt;
using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthService.Tests.Controllers;

// Unit tests for AuthController.Login with every dependency mocked.
// No real database, SMTP, or Kafka is touched — these test the controller's
// branching logic only, matching the story: "User and Admin Login".
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - Successful User Login          -> covered
//   Scenario 2 - Successful Admin Login         -> covered
//   Scenario 3 - Invalid Credentials             -> covered (both sub-cases)
//   Scenario 4 - Deleted Account                 -> covered (see note on the test)
//   Scenario 5 - Unverified Account Login        -> covered, but see IMPORTANT note below
//   Scenario 6 - Unauthorized Route Access (403) -> NOT testable at this unit-test level;
//                see AuthorizationTests.cs for what was actually found.
public class AuthControllerLoginTests
{
    private readonly Mock<IUsersRepository> _users = new();
    private readonly Mock<IEmailVerificationTokensRepository> _tokens = new();
    private readonly Mock<IPasswordResetTokensRepository> _resetTokens = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ITokenGenerator> _tokenGenerator = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IVerificationSessionService> _sessionService = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IEventPublisher> _publisher = new();

    private AuthController BuildController()
    {
        var controller = new AuthController(
            _users.Object,
            _tokens.Object,
            _resetTokens.Object,
            _passwordHasher.Object,
            new PasswordValidator(), // real instance — pure/stateless logic, not exercised by Login
            _tokenGenerator.Object,
            _email.Object,
            _sessionService.Object,
            _jwtTokenService.Object,
            _publisher.Object,
            Options.Create(new AuthSettings()),
            Options.Create(new JwtSettings { ExpiryMinutes = 60 }),
            Options.Create(new KafkaSettings()),
            Mock.Of<ILogger<AuthController>>());

        // Login() writes a cookie via HttpContext.Response and reads
        // HttpContext.RequestServices, so the controller needs a real HttpContext.
        var httpContext = new DefaultHttpContext();

        var services = new ServiceCollection();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName)
            .Returns(Environments.Development);

        services.AddSingleton<IHostEnvironment>(environment.Object);

        httpContext.RequestServices = services.BuildServiceProvider();

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static User ActiveVerifiedUser(bool isAdmin = false) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@example.com",
        PasswordHash = "hashed-correct-password",
        Name = "Test User",
        PhoneNo = "+94771234567",
        IsAdmin = isAdmin,
        IsEmailVerified = true,
        IsKicked = false,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        UpdatedAt = DateTime.UtcNow.AddDays(-10),
        DeletedAt = null
    };

    private static LoginRequest ValidLoginRequest() => new()
    {
        Email = "user@example.com",
        Password = "CorrectPassw0rd!"
    };

    private static string? GetSetCookieHeader(AuthController controller) =>
        controller.ControllerContext.HttpContext.Response.Headers["Set-Cookie"].ToString();

    // ---------- Scenario 1: Successful User Login ----------

    [Fact]
    public async Task Login_ValidRegularUser_ReturnsOk_IssuesJwtCookie_AndRedirectShapeIsUser()
    {
        var user = ActiveVerifiedUser(isAdmin: false);
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _jwtTokenService.Setup(j => j.IssueLoginToken(user)).Returns("signed-jwt-for-user");

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<LoginResponse>(ok.Value);

        Assert.Equal(user.Id, body.Id);
        Assert.Equal(user.Email, body.Email);
        Assert.False(body.IsAdmin); // client uses this flag to redirect to the user dashboard
        Assert.True(body.IsEmailVerified);

        // Credentials were checked against the hashed password, not compared in plaintext.
        _passwordHasher.Verify(h => h.Verify(ValidLoginRequest().Password, user.PasswordHash), Times.Once);

        // A signed JWT was issued for this user and set as an httpOnly cookie.
        _jwtTokenService.Verify(j => j.IssueLoginToken(user), Times.Once);
        var setCookie = GetSetCookieHeader(controller);
        Assert.Contains("auth_token=signed-jwt-for-user", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Scenario 2: Successful Admin Login ----------

    [Fact]
    public async Task Login_ValidAdminUser_ReturnsOk_WithIsAdminTrue_AndIssuesTokenForAdminUser()
    {
        var admin = ActiveVerifiedUser(isAdmin: true);
        _users.Setup(u => u.GetByEmailAsync(admin.Email, It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), admin.PasswordHash)).Returns(true);
        _jwtTokenService.Setup(j => j.IssueLoginToken(admin)).Returns("signed-jwt-for-admin");

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<LoginResponse>(ok.Value);

        // Client uses this flag to redirect to the admin dashboard instead of the user one.
        Assert.True(body.IsAdmin);

        // The token was minted from the *admin* user object, so the admin claim is embedded
        // (see JwtTokenServiceTests for direct proof the "is_admin" claim ends up in the token).
        _jwtTokenService.Verify(j => j.IssueLoginToken(
            It.Is<User>(u => u.IsAdmin)), Times.Once);
    }

    // ---------- Scenario 3: Invalid Credentials ----------

    [Fact]
    public async Task Login_UnknownEmail_ReturnsUnauthorized_WithGenericError_AndNeverChecksPassword()
    {
        _users.Setup(u => u.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((User?)null);

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);

        // Short-circuits before ever calling the hasher — no user, nothing to compare against.
        _passwordHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _jwtTokenService.Verify(j => j.IssueLoginToken(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized_WithSameGenericErrorAsUnknownEmail()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(false);

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _jwtTokenService.Verify(j => j.IssueLoginToken(It.IsAny<User>()), Times.Never);

        // Both "unknown email" and "wrong password" must return the exact same message so an
        // attacker can't use response differences to enumerate which emails are registered.
        var unknownEmailController = BuildController();
        _users.Setup(u => u.GetByEmailAsync("nobody@example.com", It.IsAny<CancellationToken>()))
              .ReturnsAsync((User?)null);
        var unknownEmailResult = await unknownEmailController.Login(
            new LoginRequest { Email = "nobody@example.com", Password = "whatever" }, CancellationToken.None);
        var unknownEmailUnauthorized = Assert.IsType<UnauthorizedObjectResult>(unknownEmailResult.Result);

        Assert.Equal(
            unauthorized.Value!.ToString(),
            unknownEmailUnauthorized.Value!.ToString());
    }

    // ---------- Scenario 4: Deleted Account ----------

    [Fact]
    public async Task Login_DeletedAccount_IsRejected_BecauseRepositoryFiltersSoftDeletedUsersOut()
    {
        // NOTE: UsersRepository.GetByEmailAsync filters "WHERE deleted_at IS NULL", so a
        // soft-deleted account simply never comes back from the repository. From the
        // controller's point of view this is indistinguishable from "no such email" — which
        // is arguably the *correct* secure behavior (it doesn't reveal that the address used
        // to belong to a real, now-deleted account) and it does satisfy "reject the login",
        // but it means Scenario 4 does NOT get a distinct "this account was deleted" message.
        // Flagging this so the team can confirm that's the intended behavior for the DoD.
        _users.Setup(u => u.GetByEmailAsync("deleted@example.com", It.IsAny<CancellationToken>()))
              .ReturnsAsync((User?)null);

        var controller = BuildController();
        var result = await controller.Login(
            new LoginRequest { Email = "deleted@example.com", Password = "OldPassword1!" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _jwtTokenService.Verify(j => j.IssueLoginToken(It.IsAny<User>()), Times.Never);
    }

    // ---------- Scenario 5: Unverified Account Login ----------

    [Fact]
    public async Task Login_UnverifiedEmail_CorrectCredentials_ReturnsForbiddenWithVerificationToken_NoJwtIssued()
    {
        // IMPORTANT — behavior mismatch vs. the story's Acceptance Criteria:
        // The AC says: "login succeeds but the JWT/session marks them as unverified, and
        // posting actions are blocked elsewhere."
        // The ACTUAL implementation instead blocks the login entirely: it returns HTTP 403
        // with a fresh verification-session token and issues NO JWT / auth cookie at all.
        // This test documents the real, current behavior so it doesn't regress silently.
        // Recommend confirming with the team whether the AC or the implementation should
        // change before this story is marked "Done".
        var user = ActiveVerifiedUser();
        user.IsEmailVerified = false;
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _sessionService.Setup(s => s.Issue(user.Id)).Returns("verify-session-token-xyz");

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);

        // No JWT/session was ever issued and no auth cookie was set — login did not succeed.
        _jwtTokenService.Verify(j => j.IssueLoginToken(It.IsAny<User>()), Times.Never);
        var setCookie = GetSetCookieHeader(controller);
        Assert.True(string.IsNullOrEmpty(setCookie));
    }

    // ---------- Bonus: suspended ("kicked") account, part of the same status-gating logic ----------

    [Fact]
    public async Task Login_KickedAccount_CorrectCredentials_ReturnsForbidden_AndDoesNotIssueJwt()
    {
        var user = ActiveVerifiedUser();
        user.IsKicked = true;
        _users.Setup(u => u.GetByEmailAsync(user.Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);

        var controller = BuildController();
        var result = await controller.Login(ValidLoginRequest(), CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
        _jwtTokenService.Verify(j => j.IssueLoginToken(It.IsAny<User>()), Times.Never);
    }
}
