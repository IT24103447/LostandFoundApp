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
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthService.Tests.Controllers;

// Unit tests for AdminController (GET /api/admin/users, POST .../kick, POST .../unkick)
// with every dependency mocked. No real database or Kafka is touched — these test the
// controller's branching logic only, matching the story: "Admin User Management".
//
// Coverage vs. the story's acceptance criteria:
//   Scenario 1 - View All Users        -> covered
//   Scenario 2 - Search/Filter Users   -> covered (parameters are passed through correctly;
//                the actual SQL filtering lives in UsersRepository, out of scope for a
//                controller-level mock test)
//   Scenario 3 - Successful Kick       -> covered. IMPORTANT: the story's title/AC literally
//                say "delete" ("the account is deleted and can no longer log in"), but the
//                implementation is a reversible soft-ban (IsKicked=true / SetKickedAsync),
//                not AuthController.DeleteMe's hard soft-delete. There's a matching
//                POST .../unkick to reverse it. "Can no longer log in" IS satisfied — Login
//                rejects IsKicked accounts with 403 — but "the account is deleted" is not
//                literally true. Flagging this so the team can confirm "kick" is the
//                intended interpretation of this AC, not literal deletion.
//   Scenario 4 - Confirmation Required -> covered
//   Scenario 5 - Access Restriction (403 for non-admins) -> NOT satisfied by the current
//                implementation. See the dedicated section near the bottom of this file and
//                AdminRouteAuthorizationTests.cs (which already documents the same defect at
//                the ActiveUserHandler level). The tests below prove the gap exists at the
//                controller-action level too: nothing in KickUser/UnkickUser/GetUsers ever
//                reads or checks the *caller's* IsAdmin flag.
public class AdminUserManagementControllerTests
{
    private readonly Mock<IUsersRepository> _users = new();
    private readonly Mock<IEventPublisher> _publisher = new();

    private AdminController BuildController(Guid? authenticatedUserId = null)
    {
        var controller = new AdminController(
            _users.Object,
            _publisher.Object,
            Options.Create(new KafkaSettings { TopicPrefix = "authsvc" }),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<AdminController>>());

        var httpContext = new DefaultHttpContext();

        // AdminController reads the caller's id from the "sub" claim, same as AuthController.
        // Note there is deliberately no IsAdmin claim being set up anywhere here — the
        // controller has no code path that would read one even if it existed.
        if (authenticatedUserId is { } uid)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, uid.ToString()) },
                authenticationType: "TestAuth"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    // GetUsers returns Ok(new { users, total, page, pageSize, totalPages }) — an anonymous
    // type, which is compiler-generated as `internal` to the AuthService assembly. Reading
    // its properties via `dynamic` from this separate test assembly throws a
    // RuntimeBinderException at runtime (no InternalsVisibleTo is declared), even though the
    // properties themselves are public. Plain reflection works fine since it only cares
    // about member accessibility, not the declaring type's — hence this helper instead of
    // `dynamic body = ok.Value!`.
    private static T GetProp<T>(object obj, string name) =>
        (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;

    private static User ActiveUser(Guid? id = null, bool isAdmin = false, string name = "Regular User", string email = "regular@example.com") => new()
    {
        Id = id ?? Guid.NewGuid(),
        Email = email,
        PasswordHash = "hashed-password",
        Name = name,
        PhoneNo = "+94771234567",
        IsAdmin = isAdmin,
        IsEmailVerified = true,
        IsKicked = false,
        CreatedAt = DateTime.UtcNow.AddDays(-10),
        UpdatedAt = DateTime.UtcNow.AddDays(-10),
        DeletedAt = null
    };

    // ---------- Scenario 1: View All Users ----------

    [Fact]
    public async Task GetUsers_NoFilters_ReturnsAllUsersMappedToAdminUserDto()
    {
        var admin = ActiveUser(isAdmin: true);
        var regular = ActiveUser(name: "Some User", email: "someuser@example.com");
        _users.Setup(u => u.SearchUsersAsync(null, null, null, null, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User> { admin, regular });
        _users.Setup(u => u.CountUsersAsync(null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(2);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.GetUsers(search: null, isKicked: null, isVerified: null, isDeleted: null, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var users = GetProp<IEnumerable<AdminUserDto>>(ok.Value!, "users");
        Assert.Equal(2, GetProp<int>(ok.Value!, "total"));
        Assert.Contains(users, u => u.Email == admin.Email);
        Assert.Contains(users, u => u.Email == regular.Email);
    }

    [Fact]
    public async Task GetUsers_DefaultPagination_UsesPageOneAndPageSizeTwenty()
    {
        var admin = ActiveUser(isAdmin: true);
        _users.Setup(u => u.SearchUsersAsync(null, null, null, null, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync(null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: admin.Id);
        await controller.GetUsers(search: null, isKicked: null, isVerified: null, isDeleted: null, ct: CancellationToken.None);

        _users.Verify(u => u.SearchUsersAsync(null, null, null, null, 20, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1)]    // page below 1 clamps up to 1
    [InlineData(-5, 1)]
    [InlineData(3, 3)]    // valid page passes through
    public async Task GetUsers_PageOutOfRange_ClampsToAtLeastOne(int requestedPage, int expectedPage)
    {
        var admin = ActiveUser(isAdmin: true);
        var expectedOffset = (expectedPage - 1) * 20;
        _users.Setup(u => u.SearchUsersAsync(null, null, null, null, 20, expectedOffset, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync(null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.GetUsers(search: null, isKicked: null, isVerified: null, isDeleted: null, page: requestedPage, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expectedPage, GetProp<int>(ok.Value!, "page"));
    }

    [Theory]
    [InlineData(0, 1)]     // below minimum clamps up
    [InlineData(500, 100)] // above maximum clamps down
    [InlineData(50, 50)]   // valid value passes through
    public async Task GetUsers_PageSizeOutOfRange_ClampsBetweenOneAndOneHundred(int requestedPageSize, int expectedPageSize)
    {
        var admin = ActiveUser(isAdmin: true);
        _users.Setup(u => u.SearchUsersAsync(null, null, null, null, expectedPageSize, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync(null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.GetUsers(search: null, isKicked: null, isVerified: null, isDeleted: null, pageSize: requestedPageSize, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expectedPageSize, GetProp<int>(ok.Value!, "pageSize"));
    }

    // ---------- Scenario 2: Search/Filter Users ----------

    [Fact]
    public async Task GetUsers_SearchTerm_IsPassedThroughToTheRepositoryUnmodified()
    {
        var admin = ActiveUser(isAdmin: true);
        _users.Setup(u => u.SearchUsersAsync("jane", null, null, null, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User> { ActiveUser(name: "Jane Doe", email: "jane@example.com") });
        _users.Setup(u => u.CountUsersAsync("jane", null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(1);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.GetUsers(search: "jane", isKicked: null, isVerified: null, isDeleted: null, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, GetProp<int>(ok.Value!, "total"));
        _users.Verify(u => u.SearchUsersAsync("jane", null, null, null, 20, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetUsers_SearchWithNoMatches_ReturnsEmptyList_NotAnError()
    {
        var admin = ActiveUser(isAdmin: true);
        _users.Setup(u => u.SearchUsersAsync("nobody-matches-this", null, null, null, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync("nobody-matches-this", null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.GetUsers(search: "nobody-matches-this", isKicked: null, isVerified: null, isDeleted: null, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, GetProp<int>(ok.Value!, "total"));
    }

    [Theory]
    [InlineData(true, null, null)]
    [InlineData(null, true, null)]
    [InlineData(null, null, true)]
    [InlineData(false, false, false)]
    public async Task GetUsers_StatusFilters_ArePassedThroughToTheRepositoryUnmodified(bool? isKicked, bool? isVerified, bool? isDeleted)
    {
        var admin = ActiveUser(isAdmin: true);
        _users.Setup(u => u.SearchUsersAsync(null, isKicked, isVerified, isDeleted, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync(null, isKicked, isVerified, isDeleted, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: admin.Id);
        await controller.GetUsers(search: null, isKicked: isKicked, isVerified: isVerified, isDeleted: isDeleted, ct: CancellationToken.None);

        _users.Verify(u => u.SearchUsersAsync(null, isKicked, isVerified, isDeleted, 20, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- Scenario 3: Successful Kick ----------
    // (See the IMPORTANT note at the top of this file re: "kick" vs. literal "delete".)

    [Fact]
    public async Task KickUser_ConfirmedOnRegularNonAdminUser_ReturnsOk_AndSetsIsKickedTrue()
    {
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser(name: "Spammy McSpamface", email: "spam@example.com");
        _users.Setup(u => u.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _users.Setup(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(target.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _users.Verify(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KickUser_Confirmed_PublishesUserKickedEventWithAdminAndTargetDetails()
    {
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser(name: "Spammy McSpamface", email: "spam@example.com");
        _users.Setup(u => u.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _users.Setup(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: admin.Id);
        await controller.KickUser(target.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        _publisher.Verify(p => p.PublishAsync(
            "authsvc.user.kicked",
            It.Is<UserKickedEvent>(e =>
                e.UserId == target.Id &&
                e.KickedBy == admin.Id &&
                e.Email == target.Email &&
                e.Name == target.Name),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KickUser_AlreadyKickedUser_ReturnsBadRequest_AndDoesNotPublishAgain()
    {
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser();
        target.IsKicked = true;
        _users.Setup(u => u.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(target.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        _users.Verify(u => u.SetKickedAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<UserKickedEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KickUser_TargetIsAdmin_ReturnsBadRequest_AndDoesNotKick()
    {
        var admin = ActiveUser(isAdmin: true);
        var otherAdmin = ActiveUser(isAdmin: true, email: "other-admin@lostandfound.com");
        _users.Setup(u => u.GetByIdAsync(otherAdmin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(otherAdmin);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(otherAdmin.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        _users.Verify(u => u.SetKickedAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KickUser_TargetIsCallerThemself_ReturnsBadRequest_AndDoesNotKick()
    {
        var admin = ActiveUser(isAdmin: true);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(admin.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        // Self-kick is rejected before even loading the target user from the repository.
        _users.Verify(u => u.GetByIdAsync(admin.Id, It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(u => u.SetKickedAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KickUser_TargetDoesNotExist_ReturnsNotFound()
    {
        var admin = ActiveUser(isAdmin: true);
        var missingId = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(missingId, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(missingId, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task KickUser_ConfirmedKick_TargetCanNoLongerLogIn()
    {
        // This is the closest controller-level proof of "the account ... can no longer log
        // in" from AC Scenario 3: SetKickedAsync flips IsKicked, and AuthController.Login
        // rejects any user with IsKicked == true with a 403. Simulate persistence so the
        // effect is visible on the same in-memory user, mirroring the pattern used in
        // ProfileControllerTests (UpdateProfileAsync callback).
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser();
        _users.Setup(u => u.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _users.Setup(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>()))
              .Callback(() => target.IsKicked = true)
              .Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: admin.Id);
        await controller.KickUser(target.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        Assert.True(target.IsKicked);
    }

    // ---------- Scenario 4: Confirmation Required ----------

    [Fact]
    public async Task KickUser_ConfirmFalse_ReturnsBadRequest_AndDoesNotKick()
    {
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser();

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.KickUser(target.Id, new ConfirmRequest { Confirm = false }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        // Confirmation is checked before the target user is even loaded.
        _users.Verify(u => u.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(u => u.SetKickedAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnkickUser_ConfirmFalse_ReturnsBadRequest_AndDoesNotUnkick()
    {
        var admin = ActiveUser(isAdmin: true);
        var target = ActiveUser();
        target.IsKicked = true;

        var controller = BuildController(authenticatedUserId: admin.Id);
        var result = await controller.UnkickUser(target.Id, new ConfirmRequest { Confirm = false }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        _users.Verify(u => u.SetKickedAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ConfirmRequest_MissingConfirmField_DefaultsFalse_NotAValidationError()
    {
        // IMPORTANT: unlike DeleteAccountRequest.Password ([Required] on a string, so an
        // empty value fails model validation), ConfirmRequest.Confirm is a plain bool with
        // [Required]. [Required] on a non-nullable bool is a no-op in ASP.NET Core model
        // validation — false is a perfectly valid bool, so a client that omits "confirm"
        // entirely gets Confirm = false silently, not a 400 from validation. It still ends
        // up rejected by KickUser's own "if (!req.Confirm)" check, so the *outcome* for
        // Scenario 4 is correct, but not for the reason the [Required] attribute suggests.
        var request = new ConfirmRequest();
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.False(request.Confirm);
    }

    // ---------- Scenario 5: Access Restriction (403 for non-admins) ----------
    //
    // CRITICAL — this AC is NOT satisfied by the current implementation. AdminController is
    // only guarded by [Authorize(Policy = "ActiveUser")], and ActiveUserHandler only checks
    // that the caller is a real, non-kicked account — it never checks IsAdmin. Nothing inside
    // GetUsers/KickUser/UnkickUser reads the caller's IsAdmin flag either; "admin" is
    // BuildController(authenticatedUserId: admin.Id) above is a fiction — the controller
    // itself would behave identically if that id belonged to a regular user.
    //
    // AdminRouteAuthorizationTests.cs already documents this exact gap at the
    // ActiveUserHandler level. The tests below prove it holds at the controller-action level
    // too, which is the more direct demonstration for THIS story (Admin User Management)
    // since its Scenario 5 explicitly calls out "admin user-management endpoints".
    //
    // Also note: LoginFormTests.RegularUser_AdminApiRequest_MustReturn403 (Selenium, from the
    // Login story) already asserts the desired 403 behavior directly and will fail against a
    // live server until this is fixed — it is not yet reconciled with the documented defect
    // here. Recommend the team either fix the authorization gap or update that assertion to
    // match current behavior, so the two test suites don't disagree with each other.

    [Fact]
    public async Task GetUsers_CalledByNonAdminId_StillSucceeds_BecauseControllerNeverChecksCallerIsAdmin()
    {
        var nonAdmin = ActiveUser(isAdmin: false, email: "not-an-admin@example.com");
        _users.Setup(u => u.SearchUsersAsync(null, null, null, null, 20, 0, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new List<User>());
        _users.Setup(u => u.CountUsersAsync(null, null, null, null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(0);

        var controller = BuildController(authenticatedUserId: nonAdmin.Id);
        var result = await controller.GetUsers(search: null, isKicked: null, isVerified: null, isDeleted: null, ct: CancellationToken.None);

        // DEFECT: per Scenario 5, this should be rejected with 403. Today it returns 200.
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task KickUser_CalledByNonAdminId_StillSucceeds_BecauseControllerNeverChecksCallerIsAdmin()
    {
        var nonAdmin = ActiveUser(isAdmin: false, email: "not-an-admin@example.com");
        var target = ActiveUser(email: "innocent-bystander@example.com");
        _users.Setup(u => u.GetByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _users.Setup(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var controller = BuildController(authenticatedUserId: nonAdmin.Id);
        var result = await controller.KickUser(target.Id, new ConfirmRequest { Confirm = true }, CancellationToken.None);

        // DEFECT: a completely non-admin caller can kick another regular user today. This is
        // more severe than a read-only information leak (GetUsers above) — it's an
        // unauthorized write / account-suspension capability. Per Scenario 5 this must be 403.
        Assert.IsType<OkObjectResult>(result);
        _users.Verify(u => u.SetKickedAsync(target.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
