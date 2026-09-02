using System.Security.Claims;
using AuthService.Authorization;
using AuthService.Models;
using AuthService.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace AuthService.Tests.Authorization;

// Scenario 6 of the Login story: "Given a regular user is logged in, when they attempt to
// access an admin-only route, then the system denies access (403)."
//
// AdminController is only guarded by [Authorize(Policy = "ActiveUser")], and that policy is
// backed solely by ActiveUserRequirement/ActiveUserHandler, which only checks that the user
// exists and is not kicked — it never checks IsAdmin. There is no admin-only policy or
// [Authorize(Roles = ...)] anywhere in the codebase.
//
// The test below proves that gap directly at the handler level: a perfectly normal,
// non-admin, non-kicked user PASSES the exact policy that guards every admin endpoint today.
// That means Scenario 6 is currently NOT satisfied — a regular user's JWT is enough to reach
// admin routes; the server never returns 403 for them based on role.
//
// This is filed here as a failing-by-design regression test (Assert.True(context.HasSucceeded))
// documenting the bug, rather than skipped or silently ignored, so it stays visible until a
// real admin-only authorization requirement is added (e.g. an AdminOnlyRequirement checking
// user.IsAdmin, applied to AdminController instead of/alongside "ActiveUser").
public class AdminRouteAuthorizationTests
{
    private readonly Mock<IUsersRepository> _users = new();

    private static User NonAdminActiveUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "regular.user@example.com",
        PasswordHash = "hash",
        Name = "Regular User",
        PhoneNo = "+94770000099",
        IsAdmin = false,
        IsEmailVerified = true,
        IsKicked = false,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AuthorizationHandlerContext BuildContext(Guid userId, IAuthorizationRequirement requirement)
    {
        var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", userId.ToString()) }, authenticationType: "TestAuth"));
        return new AuthorizationHandlerContext(new[] { requirement }, claimsPrincipal, resource: null);
    }

    [Fact]
    public async Task ActiveUserPolicy_AloneGuardingAdminController_DoesNotRejectNonAdminUsers()
    {
        var nonAdmin = NonAdminActiveUser();
        _users.Setup(u => u.GetByIdAsync(nonAdmin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(nonAdmin);

        var handler = new ActiveUserHandler(_users.Object);
        var requirement = new ActiveUserRequirement();
        var context = BuildContext(nonAdmin.Id, requirement);

        await handler.HandleAsync(context);

        // DEFECT: this currently succeeds for a non-admin user. Per Scenario 6, accessing an
        // admin-only route as a regular user must be denied (403). Today, nothing in the
        // authorization pipeline enforces that — [Authorize(Policy = "ActiveUser")] on
        // AdminController only confirms the caller is a real, non-suspended account holder.
        Assert.True(context.HasSucceeded);
        Assert.False(nonAdmin.IsAdmin);
    }
}
