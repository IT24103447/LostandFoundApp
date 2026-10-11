using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AuthService.UnitTests.Registration;

public class RegistrationControllerTests
{
    private readonly Mock<IUsersRepository> _users = new(MockBehavior.Strict);
    private readonly Mock<IEmailVerificationTokensRepository> _tokens = new(MockBehavior.Strict);
    private readonly Mock<IPasswordHasher> _hasher = new(MockBehavior.Strict);
    private readonly Mock<ITokenGenerator> _otp = new(MockBehavior.Strict);
    private readonly Mock<IEmailService> _email = new(MockBehavior.Strict);
    private readonly Mock<IVerificationSessionService> _session = new(MockBehavior.Strict);

    private AuthController Controller() => new(
        _users.Object, _tokens.Object, Mock.Of<IPasswordResetTokensRepository>(),
        _hasher.Object, new PasswordValidator(), _otp.Object, _email.Object,
        _session.Object, Mock.Of<IJwtTokenService>(), Mock.Of<IEventPublisher>(),
        Options.Create(new AuthSettings { OtpExpiryMinutes = 10 }),
        Options.Create(new JwtSettings()), Options.Create(new KafkaSettings()),
        Mock.Of<ILogger<AuthController>>())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static RegisterRequest Request() => new()
    {
        Email = "registration@example.test", Password = "Strong123",
        Name = "Registration User", PhoneNo = "+94771234567"
    };

    [Fact]
    public async Task Valid_registration_creates_unverified_user_and_requests_verification_email()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        User? saved = null;
        _users.Setup(x => x.EmailExistsAsync(request.Email, ct)).ReturnsAsync(false);
        _users.Setup(x => x.PhoneExistsAsync(request.PhoneNo, ct)).ReturnsAsync(false);
        _hasher.Setup(x => x.Hash(request.Password)).Returns("test-password-hash");
        _users.Setup(x => x.CreateAsync(It.IsAny<User>(), ct))
            .Callback<User, CancellationToken>((user, _) => saved = user)
            .Returns(Task.CompletedTask);
        _otp.Setup(x => x.GenerateCode()).Returns("123456");
        _otp.Setup(x => x.Hash("123456")).Returns("test-otp-hash");
        _tokens.Setup(x => x.CreateAsync(It.IsAny<Guid>(), "test-otp-hash", null,
            It.IsAny<DateTime>(), ct)).Returns(Task.CompletedTask);
        _email.Setup(x => x.SendAsync(request.Email, "Your Lost & Found verification code",
            It.Is<string>(body => body.Contains("123456")),
            It.Is<string?>(body => body != null && body.Contains("123456")), ct))
            .Returns(Task.CompletedTask);
        _session.Setup(x => x.Issue(It.IsAny<Guid>())).Returns("test-verification-session");
        var earliestExpiry = DateTime.UtcNow.AddMinutes(10);

        var result = await Controller().Register(request, ct);

        var response = Assert.IsType<RegisterResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.NotNull(saved);
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(request.Email, saved.Email);
        Assert.Equal(request.Name, saved.Name);
        Assert.Equal(request.PhoneNo, saved.PhoneNo);
        Assert.False(saved.IsAdmin);
        Assert.False(saved.IsEmailVerified);
        Assert.Equal("test-password-hash", saved.PasswordHash);
        Assert.NotEqual(request.Password, saved.PasswordHash);
        Assert.Equal(saved.Id, response.Id);
        Assert.False(response.IsAdmin);
        Assert.False(response.IsEmailVerified);
        Assert.Equal("test-verification-session", response.VerificationSessionToken);
        _users.Verify(x => x.CreateAsync(saved, ct), Times.Once);
        _hasher.Verify(x => x.Hash(request.Password), Times.Once);
        _otp.Verify(x => x.GenerateCode(), Times.Once);
        _otp.Verify(x => x.Hash("123456"), Times.Once);
        _tokens.Verify(x => x.CreateAsync(saved.Id, "test-otp-hash", null,
            It.Is<DateTime>(expires => expires >= earliestExpiry && expires <= DateTime.UtcNow.AddMinutes(10)), ct), Times.Once);
        _email.Verify(x => x.SendAsync(request.Email, "Your Lost & Found verification code",
            It.Is<string>(body => body.Contains("123456")),
            It.Is<string?>(body => body != null && body.Contains("123456")), ct), Times.Once);
        _session.Verify(x => x.Issue(saved.Id), Times.Once);
    }

    [Fact]
    public async Task Duplicate_email_rejects_registration_without_side_effects()
    {
        var request = Request();
        _users.Setup(x => x.EmailExistsAsync(request.Email, CancellationToken.None)).ReturnsAsync(true);

        var result = await Controller().Register(request, CancellationToken.None);

        var response = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("email already exists", System.Text.Json.JsonSerializer.Serialize(response.Value));
        _users.Verify(x => x.PhoneExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoSideEffects();
    }

    [Fact]
    public async Task Duplicate_phone_rejects_registration_without_side_effects()
    {
        var request = Request();
        _users.Setup(x => x.EmailExistsAsync(request.Email, CancellationToken.None)).ReturnsAsync(false);
        _users.Setup(x => x.PhoneExistsAsync(request.PhoneNo, CancellationToken.None)).ReturnsAsync(true);

        var result = await Controller().Register(request, CancellationToken.None);

        var response = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("phone number already exists", System.Text.Json.JsonSerializer.Serialize(response.Value));
        AssertNoSideEffects();
    }

    [Fact]
    public async Task Weak_password_returns_requirements_before_repository_access()
    {
        var request = Request();
        request.Password = "weak";

        var result = await Controller().Register(request, CancellationToken.None);

        var response = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, response.StatusCode);
        var problem = Assert.IsType<ValidationProblemDetails>(response.Value);
        Assert.Contains("Password must be at least 8 characters long.", problem.Errors["Password"]);
        _users.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(x => x.PhoneExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        AssertNoSideEffects();
    }

    private void AssertNoSideEffects()
    {
        _users.Verify(x => x.CreateAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _hasher.VerifyNoOtherCalls();
        _otp.VerifyNoOtherCalls();
        _tokens.VerifyNoOtherCalls();
        _email.VerifyNoOtherCalls();
        _session.VerifyNoOtherCalls();
    }
}
