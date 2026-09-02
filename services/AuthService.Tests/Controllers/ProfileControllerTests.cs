using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Events;
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

// Unit tests for AuthController.GetMe / UpdateMe with every dependency mocked.
// No real database, SMTP, or Kafka is touched — these test the controller's
// branching logic only, matching the story: "View/Update Profile".
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - View Profile              -> covered (GetMe_* tests)
//   Scenario 2 - Successful Update         -> covered (UpdateMe_Valid* tests)
//   Scenario 3 - Invalid Update            -> partially covered, see IMPORTANT note below
//   Scenario 4 - Access Restriction (403)  -> covered, but see IMPORTANT note below
public class ProfileControllerTests
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
            Options.Create(new KafkaSettings()),
            Mock.Of<ILogger<AuthController>>());

        var httpContext = new DefaultHttpContext();

        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        services.AddSingleton<IHostEnvironment>(environment.Object);
        httpContext.RequestServices = services.BuildServiceProvider();

        // GetMe/UpdateMe read the caller's id from the "sub" claim on HttpContext.User.
        // This simulates what JWT bearer authentication would have populated from a
        // real, validly-signed auth_token cookie.
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

    // ---------- Scenario 1: View Profile ----------

    [Fact]
    public async Task GetMe_AuthenticatedActiveVerifiedUser_ReturnsNameEmailAndPhone()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.GetMe(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<UserProfileDto>(ok.Value);

        Assert.Equal(user.Name, body.Name);
        Assert.Equal(user.Email, body.Email);
        Assert.Equal(user.PhoneNo, body.PhoneNo);
    }

    [Fact]
    public async Task GetMe_NoAuthenticatedUser_ReturnsUnauthorized()
    {
        // No "sub" claim on HttpContext.User — equivalent to no/invalid auth_token cookie.
        // NOTE: unlike UpdateMe, the GetMe action has no [Authorize] attribute — it relies
        // entirely on manually reading the claim and returning 401 itself. Functionally this
        // still rejects unauthenticated callers today, but it's inconsistent with UpdateMe and
        // worth flagging: if the JWT bearer scheme is ever changed to only populate
        // HttpContext.User for [Authorize]-attributed actions, this endpoint would silently
        // start behaving differently. Recommend adding [Authorize] here too for consistency.
        var controller = BuildController(authenticatedUserId: null);

        var result = await controller.GetMe(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _users.Verify(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMe_KickedUser_ReturnsForbidden()
    {
        var user = ActiveVerifiedUser();
        user.IsKicked = true;
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var controller = BuildController(authenticatedUserId: user.Id);
        var result = await controller.GetMe(CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
    }

    // ---------- Scenario 2: Successful Update ----------

    [Fact]
    public async Task UpdateMe_ValidNameAndPhone_PersistsAndReturnsUpdatedProfileImmediately()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(user.Id, "+94770009999", It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);

        // Simulate persistence: mutate the same in-memory user so the controller's
        // second GetByIdAsync() reload reflects the change, like a real DB would.
        _users.Setup(u => u.UpdateProfileAsync(user.Id, "Updated Name", "+94770009999", It.IsAny<CancellationToken>()))
              .Callback(() =>
              {
                  user.Name = "Updated Name";
                  user.PhoneNo = "+94770009999";
              })
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        var request = new UpdateProfileRequest { Name = "Updated Name", PhoneNo = "+94770009999" };

        var result = await controller.UpdateMe(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<UserProfileDto>(ok.Value);
        Assert.Equal("Updated Name", body.Name);
        Assert.Equal("+94770009999", body.PhoneNo);

        _users.Verify(u => u.UpdateProfileAsync(user.Id, "Updated Name", "+94770009999", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMe_FieldsActuallyChanged_PublishesProfileUpdatedEvent()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(user.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.UpdateProfileAsync(user.Id, "New Name", user.PhoneNo, It.IsAny<CancellationToken>()))
              .Callback(() => user.Name = "New Name")
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        var request = new UpdateProfileRequest { Name = "New Name", PhoneNo = user.PhoneNo };

        await controller.UpdateMe(request, CancellationToken.None);

        _publisher.Verify(p => p.PublishAsync(
            It.Is<string>(topic => topic.EndsWith("user.profile_updated")),
            It.Is<UserProfileUpdatedEvent>(e => e.UserId == user.Id && e.UpdatedFields.Contains("name")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMe_NoFieldsActuallyChanged_DoesNotPublishEvent()
    {
        // Saving the exact same name/phone the user already has shouldn't fire a
        // "profile updated" event downstream.
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(user.Id, user.PhoneNo, It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.UpdateProfileAsync(user.Id, user.Name, user.PhoneNo, It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        var request = new UpdateProfileRequest { Name = user.Name, PhoneNo = user.PhoneNo };

        await controller.UpdateMe(request, CancellationToken.None);

        _publisher.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<UserProfileUpdatedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateMe_PhoneAlreadyUsedByAnotherUser_ReturnsConflict_AndDoesNotPersist()
    {
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(user.Id, "+94779999999", It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

        var controller = BuildController(authenticatedUserId: user.Id);
        var request = new UpdateProfileRequest { Name = "New Name", PhoneNo = "+94779999999" };

        var result = await controller.UpdateMe(request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(409, conflict.StatusCode);
        _users.Verify(u => u.UpdateProfileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateMe_UserBecomesKickedBetweenReadAndReload_ReturnsForbidden_DespiteDataAlreadyBeingPersisted()
    {
        // QUIRK worth flagging: UpdateMe calls _users.UpdateProfileAsync BEFORE re-checking
        // IsKicked/IsEmailVerified on the reloaded user. If the account is suspended in the
        // moment between the two reads (race with an admin action), the update is already
        // written to the DB, but the client still receives a 403 as if nothing was saved.
        // This is a minor UX/consistency edge case, not a security issue, but the team should
        // confirm this ordering is intentional.
        var user = ActiveVerifiedUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(user.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.UpdateProfileAsync(user.Id, "New Name", user.PhoneNo, It.IsAny<CancellationToken>()))
              .Callback(() =>
              {
                  user.Name = "New Name";
                  user.IsKicked = true; // simulates a suspension landing mid-request
              })
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: user.Id);
        var request = new UpdateProfileRequest { Name = "New Name", PhoneNo = user.PhoneNo };

        var result = await controller.UpdateMe(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, objectResult.StatusCode);
        // The write already happened despite the 403 response:
        _users.Verify(u => u.UpdateProfileAsync(user.Id, "New Name", user.PhoneNo, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- Scenario 3: Invalid Update ----------
    //
    // IMPORTANT — this scenario is only PARTIALLY testable at the controller-unit-test level.
    // UpdateProfileRequest's validation (name length, phone format) is enforced via
    // [Required]/[MaxLength]/[RegularExpression] DataAnnotations, which [ApiController]
    // validates automatically and short-circuits with an HTTP 400 BEFORE the action method
    // ever runs. Calling controller.UpdateMe(...) directly in a unit test bypasses that
    // pipeline entirely, so invalid input reaches the mocked repository unless the request's
    // own field-level validity is asserted separately (below). The behavior is exercised
    // end-to-end by the Selenium test (ProfileFormTests.SaveProfile_InvalidPhoneFormat_*).

    [Theory]
    [InlineData("", false)]                                  // required
    [InlineData("ok", true)]                                  // fine
    public void UpdateProfileRequest_NameRequired_ValidatesAsExpected(string name, bool expectedValid)
    {
        var request = new UpdateProfileRequest { Name = name, PhoneNo = "+94771234567" };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
            Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateProfileRequest.Name)));
    }

    [Fact]
    public void UpdateProfileRequest_NameOver150Characters_FailsMaxLengthValidation()
    {
        var request = new UpdateProfileRequest { Name = new string('a', 151), PhoneNo = "+94771234567" };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateProfileRequest.Name)));
    }

    [Theory]
    [InlineData("0771234567")]     // missing leading '+'
    [InlineData("+94")]            // too short
    [InlineData("+abcdefgh1234")]  // non-numeric
    [InlineData("")]               // required
    public void UpdateProfileRequest_InvalidPhoneFormats_FailRegexValidation(string phoneNo)
    {
        var request = new UpdateProfileRequest { Name = "Valid Name", PhoneNo = phoneNo };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(UpdateProfileRequest.PhoneNo)));
    }

    [Fact]
    public void UpdateProfileRequest_ValidE164Phone_PassesValidation()
    {
        var request = new UpdateProfileRequest { Name = "Valid Name", PhoneNo = "+94771234567" };
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
    }

    // ---------- Scenario 4: Access Restriction ----------
    //
    // IMPORTANT — behavior mismatch vs. the story's Acceptance Criteria:
    // The AC describes a user "attempting to access or edit another user's profile via a
    // manipulated request" and expects a 403. In the ACTUAL implementation there is no
    // request field or route parameter that names a target user at all — UpdateMe/GetMe
    // always operate on whichever user id is embedded in the caller's own signed JWT
    // ("sub" claim). There is structurally no way to "target" another account through the
    // request body or URL. The two tests below instead prove the two ways this protection
    // actually manifests in code:
    //   1. A valid token always resolves to (and only ever mutates) its own owner's row,
    //      regardless of what's in the request body.
    //   2. A token that doesn't map to a real user is rejected with 401 (Unauthorized) —
    //      NOT 403 (Forbidden) as the AC literally states. Since a tampered/forged JWT would
    //      fail signature validation before ever reaching this code, 401 is what a client
    //      attempting the attack described in the AC would actually observe. Recommend the
    //      team confirm whether the AC's "403" should be corrected to "401", to match how
    //      ASP.NET's JWT bearer authentication actually reports this failure mode.

    [Fact]
    public async Task UpdateMe_AlwaysOperatesOnCallersOwnIdFromToken_RegardlessOfRequestBody()
    {
        var caller = ActiveVerifiedUser();
        var otherUserId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(caller.Id, It.IsAny<CancellationToken>())).ReturnsAsync(caller);
        _users.Setup(u => u.PhoneExistsForOtherUserAsync(caller.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        _users.Setup(u => u.UpdateProfileAsync(caller.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: caller.Id);
        // UpdateProfileRequest has no "userId"/"targetUserId" field to manipulate at all —
        // this is the whole point being demonstrated.
        var request = new UpdateProfileRequest { Name = "Attempted Name", PhoneNo = "+94771111111" };

        await controller.UpdateMe(request, CancellationToken.None);

        _users.Verify(u => u.UpdateProfileAsync(caller.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.UpdateProfileAsync(otherUserId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateMe_TokenSubDoesNotMatchAnyExistingUser_ReturnsUnauthorized_NotForbidden()
    {
        var unknownUserId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(unknownUserId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var controller = BuildController(authenticatedUserId: unknownUserId);
        var request = new UpdateProfileRequest { Name = "Doesn't Matter", PhoneNo = "+94771234567" };

        var result = await controller.UpdateMe(request, CancellationToken.None);

        // See IMPORTANT note above: AC says 403, actual behavior is 401.
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
        _users.Verify(u => u.UpdateProfileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMe_TokenSubDoesNotMatchAnyExistingUser_ReturnsUnauthorized_NotForbidden()
    {
        var unknownUserId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(unknownUserId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var controller = BuildController(authenticatedUserId: unknownUserId);
        var result = await controller.GetMe(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }
}
