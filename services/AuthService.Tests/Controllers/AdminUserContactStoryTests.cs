using System.Security.Claims;
using AuthService.Configuration;
using AuthService.Controllers;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AuthService.Tests.Controllers;

// Unit tests for AdminController.GetUserContact — GET /api/admin/users/{id:guid} — the second
// half of Story LF-79 ("admin endpoints for item listings and user specific attributes by id").
// Every dependency is mocked; no database and no Kafka are touched.
//
// Before this file the endpoint had zero coverage anywhere in the repository. The frontend
// consumes it (frontend/src/features/admin/api/matchAppeals.ts:77), so it is live in production
// while being untested.
//
// A note on the return type, because it is easy to get wrong here. GetUserContact returns
// ActionResult<AdminUserContactDto>, not IActionResult, and the two results are reached
// differently:
//
//   return Ok(dto)          -> Ok() is ControllerBase.Ok<TValue>, which returns an OkObjectResult.
//                              Converting that to ActionResult<T> uses the implicit
//                              ActionResult<T>(ActionResult) operator, so .Result holds the
//                              OkObjectResult and the DTO sits at ok.Value.
//   return NotFound(obj)    -> .Result holds a NotFoundObjectResult; .Value stays null.
//
// Both branches therefore assert through .Result, the same as AdminUserManagementControllerTests.
// Reaching for .Value directly (the other ActionResult<T> convention, used when a bare T is
// returned) yields null and fails.
//
// These tests call the controller directly, which bypasses the [Authorize(Policy = "AdminOnly")]
// filter. Authorization for this route is proved end-to-end in
// Integration/AdminUserContactApiIntegrationTests.cs instead.
public class AdminUserContactStoryTests
{
    private readonly Mock<IUsersRepository> _users = new();
    private readonly Mock<IEventPublisher> _publisher = new();

    private AdminController BuildController()
    {
        var controller = new AdminController(
            _users.Object,
            _publisher.Object,
            Options.Create(new KafkaSettings { TopicPrefix = "authsvc" }),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<AdminController>>());

        // GetUserContact never reads the caller's claims — the AdminOnly policy handler does that
        // before the action runs. An empty principal is therefore the honest setup here.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        return controller;
    }

    // The 404 body is `new { error = UserNotFoundMessage }` — a compiler-generated type that is
    // `internal` to the AuthService assembly. Reading it via `dynamic` from this separate test
    // assembly throws RuntimeBinderException because no InternalsVisibleTo is declared, even
    // though the property itself is public. Plain reflection only cares about member
    // accessibility rather than the declaring type's, hence this helper.
    private static T GetProp<T>(object obj, string name) =>
        (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;

    private static User ContactUser(
        Guid? id = null,
        string name = "Kumari Perera",
        string email = "kumari@example.com",
        string phoneNo = "+94771234567") => new()
        {
            Id = id ?? Guid.NewGuid(),
            Email = email,
            PasswordHash = "hashed-password",
            Name = name,
            PhoneNo = phoneNo,
            IsAdmin = false,
            IsEmailVerified = true,
            IsKicked = false,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10),
            DeletedAt = null
        };

    // ---------- Scenario 1: Read a user's contact details ----------

    [Fact]
    public async Task GetUserContact_ExistingUser_ReturnsNameEmailAndPhoneNo()
    {
        var user = ContactUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var result = await BuildController().GetUserContact(user.Id, CancellationToken.None);

        // See the file header: .Result holds the OkObjectResult, the DTO is at ok.Value.
        Assert.Null(result.Value);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(200, ok.StatusCode);
        var dto = Assert.IsType<AdminUserContactDto>(ok.Value);
        Assert.Equal("Kumari Perera", dto.Name);
        Assert.Equal("kumari@example.com", dto.Email);
        Assert.Equal("+94771234567", dto.PhoneNo);
    }

    // The DTO is deliberately narrower than AdminUserDto (the list route's shape). This guards
    // against a future edit widening it back out to include Id, IsAdmin, IsKicked or
    // PasswordHash — none of which an admin reading a contact card needs.
    [Fact]
    public void AdminUserContactDto_ExposesExactlyNameEmailAndPhoneNo()
    {
        var propertyNames = typeof(AdminUserContactDto)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Email", "Name", "PhoneNo" }, propertyNames);
    }

    // An empty phone_no column must surface as "" rather than becoming null. The DTO initialises
    // every property to string.Empty and the mapping copies the value verbatim, so this pins the
    // guarantee the frontend relies on when it renders the contact card.
    [Fact]
    public async Task GetUserContact_EmptyPhoneNo_ReturnsEmptyStringRatherThanNull()
    {
        var user = ContactUser(phoneNo: string.Empty);
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var result = await BuildController().GetUserContact(user.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AdminUserContactDto>(ok.Value);
        Assert.NotNull(dto.PhoneNo);
        Assert.Equal(string.Empty, dto.PhoneNo);
    }

    // This endpoint is a pure read. Nothing should reach Kafka — contrast KickUser/UnkickUser,
    // which do publish user.kicked / user.unkicked.
    [Fact]
    public async Task GetUserContact_PublishesNoEvent()
    {
        var user = ContactUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        await BuildController().GetUserContact(user.Id, CancellationToken.None);

        // IEventPublisher.PublishAsync is generic, so a targeted Verify(...) cannot match every
        // possible T. VerifyNoOtherCalls with nothing else verified is the precise assertion here:
        // zero invocations of any shape.
        _publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetUserContact_ForwardsTheCancellationTokenToTheRepository()
    {
        using var cts = new CancellationTokenSource();
        var user = ContactUser();
        _users.Setup(u => u.GetByIdAsync(user.Id, cts.Token)).ReturnsAsync(user);

        await BuildController().GetUserContact(user.Id, cts.Token);

        _users.Verify(u => u.GetByIdAsync(user.Id, cts.Token), Times.Once);
    }

    // ---------- Scenario 2: Unknown user ----------

    [Fact]
    public async Task GetUserContact_UnknownId_ReturnsNotFoundWithUserNotFoundError()
    {
        var unknown = Guid.NewGuid();
        _users.Setup(u => u.GetByIdAsync(unknown, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var result = await BuildController().GetUserContact(unknown, CancellationToken.None);

        // Not-found sets .Result, leaving .Value null — the mirror image of the success path.
        Assert.Null(result.Value);
        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal("User not found.", GetProp<string>(notFound.Value!, "error"));
    }

    // UsersRepository.GetByIdAsync hard-codes `AND deleted_at IS NULL`, so a soft-deleted user
    // reaches the controller as null and is indistinguishable from a user that never existed.
    // This test documents that the controller behaves correctly given what it is handed; the
    // consequence (soft-deleted users are unlistable-by-id even though the list route can show
    // them) is recorded in QA_Documentation/Story-LF-79/known-gaps.md.
    [Fact]
    public async Task GetUserContact_SoftDeletedUser_ReturnsNotFoundBecauseTheRepositoryFiltersItOut()
    {
        var deleted = ContactUser();
        _users.Setup(u => u.GetByIdAsync(deleted.Id, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var result = await BuildController().GetUserContact(deleted.Id, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("User not found.", GetProp<string>(notFound.Value!, "error"));
    }

    // ---------- Known gap: kicked users are still readable ----------

    // GetByIdAsync filters deleted_at but NOT is_kicked, so a suspended account still returns
    // 200 with full contact details. This test deliberately pins the CURRENT behaviour so that a
    // future change to tighten it surfaces as a failing test rather than a silent behaviour shift.
    // Tracked in known-gaps.md as a probable privacy gap, not as intended design.
    [Fact]
    public async Task GetUserContact_KickedUser_StillReturnsFullContactDetails()
    {
        var kicked = ContactUser();
        kicked.IsKicked = true;
        _users.Setup(u => u.GetByIdAsync(kicked.Id, It.IsAny<CancellationToken>())).ReturnsAsync(kicked);

        var result = await BuildController().GetUserContact(kicked.Id, CancellationToken.None);

        Assert.Null(result.Value);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AdminUserContactDto>(ok.Value);
        Assert.Equal("Kumari Perera", dto.Name);
        Assert.Equal("kumari@example.com", dto.Email);
        Assert.Equal("+94771234567", dto.PhoneNo);
    }
}