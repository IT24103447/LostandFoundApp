using System.Security.Claims;
using System.Text.Json;
using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AuthService.UnitTests.Support;

// A fresh context per test: real controller logic, strict external dependencies.
internal sealed class AuthControllerTestContext
{
    public Mock<IUsersRepository> Users { get; } = new(MockBehavior.Strict);
    public Mock<IEmailVerificationTokensRepository> Tokens { get; } = new(MockBehavior.Strict);
    public Mock<IPasswordHasher> Hasher { get; } = new(MockBehavior.Strict);
    public Mock<ITokenGenerator> Otp { get; } = new(MockBehavior.Strict);
    public Mock<IEmailService> Email { get; } = new(MockBehavior.Strict);
    public Mock<IVerificationSessionService> Session { get; } = new(MockBehavior.Strict);
    public Mock<IJwtTokenService> Jwt { get; } = new(MockBehavior.Strict);
    public Mock<IEventPublisher> Publisher { get; } = new(MockBehavior.Strict);
    public AuthSettings Auth { get; } = new() { OtpExpiryMinutes = 10, MaxOtpAttempts = 10, ResendCooldownSeconds = 60 };
    public JwtSettings JwtSettings { get; } = new() { ExpiryMinutes = 30 };
    public const string TopicPrefix = "unit-test";

    public AuthController Controller(bool production = false, string? subject = null,
        string claimType = "sub")
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(x => x.EnvironmentName).Returns(production ? Environments.Production : Environments.Development);
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(environment.Object).BuildServiceProvider()
        };
        if (subject is not null)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, subject)], "UnitTest"));
        return new AuthController(Users.Object, Tokens.Object, Mock.Of<IPasswordResetTokensRepository>(),
            Hasher.Object, new PasswordValidator(), Otp.Object, Email.Object, Session.Object, Jwt.Object,
            Publisher.Object, Options.Create(Auth), Options.Create(JwtSettings),
            Options.Create(new KafkaSettings { TopicPrefix = TopicPrefix }), Mock.Of<ILogger<AuthController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    public static User User(bool verified = true) => new()
    {
        Id = Guid.NewGuid(), Email = "story@example.test", Name = "Story User",
        PhoneNo = "+94771234567", PasswordHash = "stored-password-hash",
        IsEmailVerified = verified, CreatedAt = DateTime.UtcNow.AddDays(-1)
    };

    public static JsonElement Body(ObjectResult result) => JsonSerializer.SerializeToElement(result.Value);

    public void AssertAllCallsVerified()
    {
        Users.VerifyAll(); Tokens.VerifyAll(); Hasher.VerifyAll(); Otp.VerifyAll();
        Email.VerifyAll(); Session.VerifyAll(); Jwt.VerifyAll(); Publisher.VerifyAll();
        Users.VerifyNoOtherCalls(); Tokens.VerifyNoOtherCalls(); Hasher.VerifyNoOtherCalls();
        Otp.VerifyNoOtherCalls(); Email.VerifyNoOtherCalls(); Session.VerifyNoOtherCalls();
        Jwt.VerifyNoOtherCalls(); Publisher.VerifyNoOtherCalls();
    }
}
