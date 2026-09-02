using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Models.Events;
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

// Unit tests for AuthController.DeleteMe (DELETE /api/auth/me) with every dependency
// mocked. No real database, SMTP, or Kafka is touched — these test the controller's
// branching logic only, matching the story: "Delete Account".
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - Successful Deletion        -> covered
//   Scenario 2 - Confirmation Required       -> partially covered at this layer, see the
//                IMPORTANT note above the Scenario 2 tests below
//   Scenario 3 - Incorrect Password          -> covered
//   Scenario 4 - Post-Deletion Login Attempt -> NOT re-tested here; this is a Login-side
//                behavior already covered by AuthControllerLoginTests
//                (Login_DeletedAccount_IsRejected_BecauseRepositoryFiltersSoftDeletedUsersOut).
//                DeleteAccountControllerTests only proves the account row is actually
//                soft-deleted (Deletion_PersistsSoftDeleteBeforePublishingEvent below); the
//                Selenium suite proves the two behaviors chain together end-to-end.
public class DeleteAccountControllerTests
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

    private AuthController BuildController(Guid? authenticatedUserId = null)
    {
        var controller = new AuthController(
            _users.Object,
            _tokens.Object,
            _resetTokens.Object,
            _passwordHasher.Object,
            new PasswordValidator(), // real instance — pure/stateless logic, not exercised here
            _tokenGenerator.Object,
            _email.Object,
            _sessionService.Object,
            _jwtTokenService.Object,
            _publisher.Object,
            Options.Create(new AuthSettings()),
            Options.Create(new JwtSettings { ExpiryMinutes = 60 }),
            Options.Create(new KafkaSettings { TopicPrefix = "authsvc" }),
            Mock.Of<ILogger<AuthController>>());

        var httpContext = new DefaultHttpContext();

        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        services.AddSingleton<IHostEnvironment>(environment.Object);
        httpContext.RequestServices = services.BuildServiceProvider();

        // DeleteMe reads the caller's id from the "sub" claim on HttpContext.User, exactly
        // like GetMe/UpdateMe — this simulates what JWT bearer authentication would have
        // populated from a real, validly-signed auth_token cookie.
        if (authenticatedUserId is { } uid)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(JwtRegisteredClaimNames.Sub, uid.ToString()) },
                authenticationType: "TestAuth"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static User ActiveVerifiedUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Email = "user@example.com",
        PasswordHash = "hashed-password",
        Name = "Original Name",
        PhoneNo = "+94771234567",
        IsAdmin = false,
        IsEmailVerified = true,
        IsKicked = false,
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        UpdatedAt = DateTime.UtcNow.AddDays(-30),
        DeletedAt = null
    };

    private static string? GetSetCookieHeader(AuthController controller) =>
        controller.ControllerContext.HttpContext.Response.Headers["Set-Cookie"].ToString();

    // ---------- Scenario 1: Successful Deletion ----------

    [Fact]
    public async Task DeleteMe_CorrectPassword_ReturnsOk_AndSoftDeletesTheAccount()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify("CorrectPassw0rd!", user.PasswordHash)).Returns(true);
        _resetTokens.Setup(r => r.DeleteForUserAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _tokens.Setup(t => t.DeleteForUserAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _users.Setup(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _users.Verify(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteMe_CorrectPassword_RemovesOutstandingPasswordResetAndVerificationTokens()
    {
        // A deleted account shouldn't leave live reset/verification tokens lying around that
        // could later be replayed against a recreated account with the same email.
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _users.Setup(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        _resetTokens.Verify(r => r.DeleteForUserAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
        _tokens.Verify(t => t.DeleteForUserAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteMe_CorrectPassword_PublishesUserDeletedEventWithMatchingDetails()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _users.Setup(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        _publisher.Verify(p => p.PublishAsync(
            "authsvc.user.deleted",
            It.Is<UserDeletedEvent>(e =>
                e.UserId == user.Id &&
                e.Email == user.Email &&
                e.Name == user.Name &&
                e.Phone == user.PhoneNo),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteMe_CorrectPassword_ClearsTheAuthCookie_SoTheSessionEndsImmediately()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _users.Setup(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        var setCookie = GetSetCookieHeader(controller);
        Assert.Contains("auth_token=", setCookie);
        // Deleting a cookie is expressed as an immediately-expired Set-Cookie header.
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteMe_CorrectPassword_DeletesRepositoryStateBeforePublishingTheEvent()
    {
        // Ordering matters for correctness: if the Kafka publish happened first and a
        // downstream consumer (e.g. something that emails the user or purges related data)
        // raced ahead of the soft-delete actually committing, it could observe a
        // still-"live" account. Assert the repository call happens strictly before publish.
        var user = ActiveVerifiedUser();
        var callOrder = new List<string>();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(true);
        _users.Setup(u => u.SoftDeleteAsync(user.Id, It.IsAny<CancellationToken>()))
              .Callback(() => callOrder.Add("soft-delete"))
              .Returns(Task.CompletedTask);
        _publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<UserDeletedEvent>(), It.IsAny<CancellationToken>()))
              .Callback(() => callOrder.Add("publish"));

        var controller = BuildController(authenticatedUserId: user.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        Assert.Equal(new[] { "soft-delete", "publish" }, callOrder);
    }

    // ---------- Scenario 2: Confirmation Required ----------
    //
    // IMPORTANT — this scenario is a UI/flow requirement ("the confirmation dialog appears",
    // "must re-enter their password", "shown a warning about the consequences") that has no
    // controller-level equivalent: DeleteMe has exactly one request shape, DeleteAccountRequest
    // { Password }, and always requires it. There is no separate "open the dialog" step or
    // warning-text field at the API layer — those live entirely in DeleteAccountSection.tsx.
    // What IS testable here is the API-level half of the contract: a password is mandatory,
    // and omitting it is rejected before deletion ever runs. The UI-rendered warning text and
    // dialog behavior are covered by the Selenium suite instead
    // (DeleteAccountFormTests.DeleteAccount_PageShowsPasswordPromptAndConsequenceWarning).

    [Fact]
    public void DeleteAccountRequest_MissingPassword_FailsRequiredValidation()
    {
        var request = new DeleteAccountRequest { Password = "" };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(DeleteAccountRequest.Password)));
    }

    // ---------- Scenario 3: Incorrect Password on Confirmation ----------

    [Fact]
    public async Task DeleteMe_IncorrectPassword_ReturnsBadRequest_AndDoesNotDeleteAnything()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify("WrongPassword!", user.PasswordHash)).Returns(false);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "WrongPassword!" }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);

        _users.Verify(u => u.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _resetTokens.Verify(r => r.DeleteForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(t => t.DeleteForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<UserDeletedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_IncorrectPassword_DoesNotClearTheAuthCookie_SessionStaysActive()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), user.PasswordHash)).Returns(false);

        var controller = BuildController(authenticatedUserId: user.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "WrongPassword!" }, CancellationToken.None);

        var setCookie = GetSetCookieHeader(controller);
        Assert.True(string.IsNullOrEmpty(setCookie));
    }

    // ---------- Additional guardrails not explicitly enumerated in the AC, but implemented ----------

    [Fact]
    public async Task DeleteMe_NoAuthenticatedUser_ReturnsUnauthorized_AndDoesNotTouchTheRepository()
    {
        var controller = BuildController(authenticatedUserId: null);

        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "whatever" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        _users.Verify(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_AdminAccount_ReturnsForbidden_AndDoesNotDeleteAnything()
    {
        var admin = ActiveVerifiedUser();
        admin.IsAdmin = true;
        _users.Setup(u => u.GetByIdAsync(admin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(admin);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "whatever" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        // Password is never even checked for an admin — the role check short-circuits first.
        _passwordHasher.Verify(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _users.Verify(u => u.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_KickedUser_ReturnsForbidden_AndDoesNotDeleteAnything()
    {
        var user = ActiveVerifiedUser();
        user.IsKicked = true;
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "whatever" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        _users.Verify(u => u.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_UnverifiedEmail_ReturnsUnauthorized_AndDoesNotDeleteAnything()
    {
        var user = ActiveVerifiedUser();
        user.IsEmailVerified = false;
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "whatever" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        _users.Verify(u => u.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_TokenSubDoesNotMatchAnyExistingUser_ReturnsUnauthorized()
    {
        var unknownUserId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(unknownUserId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var controller = BuildController(authenticatedUserId: unknownUserId);
        var result = await controller.DeleteMe(new DeleteAccountRequest { Password = "whatever" }, CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        _users.Verify(u => u.SoftDeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMe_AlwaysOperatesOnCallersOwnIdFromToken_NeverATargetFromTheRequestBody()
    {
        // Mirrors UpdateMe_AlwaysOperatesOnCallersOwnIdFromToken_RegardlessOfRequestBody in
        // ProfileControllerTests: DeleteAccountRequest has no user-id field to manipulate at
        // all, so there's structurally no way for a caller to delete someone else's account.
        var caller = ActiveVerifiedUser();
        var otherUserId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(caller.Id, It.IsAny<CancellationToken>())).ReturnsAsync(caller);
        _passwordHasher.Setup(h => h.Verify(It.IsAny<string>(), caller.PasswordHash)).Returns(true);
        _users.Setup(u => u.SoftDeleteAsync(caller.Id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: caller.Id);
        await controller.DeleteMe(new DeleteAccountRequest { Password = "CorrectPassw0rd!" }, CancellationToken.None);

        _users.Verify(u => u.SoftDeleteAsync(caller.Id, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.SoftDeleteAsync(otherUserId, It.IsAny<CancellationToken>()), Times.Never);
    }
}
