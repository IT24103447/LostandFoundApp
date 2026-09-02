using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthService.Tests.Controllers;

// Unit tests for AuthController.Register with every dependency mocked.
// No real database, SMTP, or Kafka is touched — these test the controller's
// branching logic only. Covers Scenarios 1 (happy path), 2 (duplicate email/phone),
// and 3 (weak password) from the User Registration story.
public class AuthControllerRegisterTests
{
    private readonly Mock<IUsersRepository> _users = new();
    private readonly Mock<IEmailVerificationTokensRepository> _tokens = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ITokenGenerator> _tokenGenerator = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IVerificationSessionService> _sessionService = new();
    private readonly Mock<IJwtTokenService> _jwtTokenService = new();
    private readonly Mock<IEventPublisher> _publisher = new();

    private AuthController BuildController()
    {
        return new AuthController(
            _users.Object,
            _tokens.Object,
            Mock.Of<IPasswordResetTokensRepository>(),
            _passwordHasher.Object,
            new PasswordValidator(), // real instance — it's pure/stateless logic
            _tokenGenerator.Object,
            _email.Object,
            _sessionService.Object,
            _jwtTokenService.Object,
            _publisher.Object,
            Options.Create(new AuthSettings()),
            Options.Create(new JwtSettings { ExpiryMinutes = 60 }),
            Options.Create(new KafkaSettings()),
            Mock.Of<ILogger<AuthController>>());
    }

    private static RegisterRequest ValidRequest() => new()
    {
        Email = "newuser@example.com",
        Password = "Str0ngPass1",
        Name = "New User",
        PhoneNo = "+94771234567"
    };

    // ---------- Scenario 3: Weak Password ----------

    [Fact]
    public async Task Register_WeakPassword_ReturnsValidationProblem_AndNeverChecksEmail()
    {
        var controller = BuildController();
        var req = ValidRequest();
        req.Password = "weak"; // fails complexity rules

        var result = await controller.Register(req, CancellationToken.None);

        var problem = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, problem.StatusCode);

        // Blocks submission before even checking the DB.
        _users.Verify(u => u.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 2: Duplicate Email / Phone ----------

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict_AndDoesNotCreateUser()
    {
        _users.Setup(u => u.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var controller = BuildController();
        var result = await controller.Register(ValidRequest(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _users.Verify(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_DuplicatePhone_ReturnsConflict_AndDoesNotCreateUser()
    {
        _users.Setup(u => u.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.PhoneExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var controller = BuildController();
        var result = await controller.Register(ValidRequest(), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        _users.Verify(u => u.CreateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- Scenario 1: Successful Registration ----------

    [Fact]
    public async Task Register_ValidData_CreatesUnverifiedUser_SendsEmail_ReturnsOk()
    {
        _users.Setup(u => u.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.PhoneExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _passwordHasher.Setup(h => h.Hash(It.IsAny<string>())).Returns("hashed-password-value");
        _tokenGenerator.Setup(t => t.GenerateCode()).Returns("123456");
        _tokenGenerator.Setup(t => t.Hash(It.IsAny<string>())).Returns("code-hash");
        _sessionService.Setup(s => s.Issue(It.IsAny<Guid>())).Returns("session-token-abc");

        var controller = BuildController();
        var req = ValidRequest();

        var result = await controller.Register(req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<RegisterResponse>(ok.Value);

        Assert.Equal(req.Email, body.Email);
        Assert.False(body.IsEmailVerified); // role/status = unverified per acceptance criteria
        Assert.False(body.IsAdmin);         // role = "User", not admin
        Assert.Equal("session-token-abc", body.VerificationSessionToken);

        // User was actually created, with a hashed password — never the plaintext one.
        _users.Verify(u => u.CreateAsync(
            It.Is<User>(usr => usr.PasswordHash == "hashed-password-value" && usr.PasswordHash != req.Password),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verification email was sent.
        _email.Verify(e => e.SendAsync(
            req.Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // NOTE: the race-condition path in AuthController (catching MySqlException with
    // Number == 1062, when two concurrent requests both pass the EmailExistsAsync
    // pre-check) is intentionally NOT unit tested here. MySqlException doesn't have
    // an accessible public constructor to fake that error safely, and faking it with
    // reflection is brittle. That path is better proven with a real integration test
    // that fires two concurrent /register calls with the same email at a real MySQL
    // instance — see the integration test project instead.
}
