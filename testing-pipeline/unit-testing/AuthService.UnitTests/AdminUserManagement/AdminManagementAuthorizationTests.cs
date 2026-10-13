using System.Security.Claims;
using AuthService.Authorization;
using AuthService.Controllers;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Authorization;
using Moq;

namespace AuthService.UnitTests.AdminUserManagement;

public class AdminManagementAuthorizationTests
{
    [Theory]
    [InlineData("ActiveAdmin", true)]
    [InlineData("RegularUser", false)]
    [InlineData("ForgedAdminClaim", false)]
    [InlineData("MissingAccount", false)]
    [InlineData("SuspendedAdmin", false)]
    [InlineData("DeletedAdmin", false)]
    [InlineData("InvalidIdentity", false)]
    public async Task User_management_policy_checks_actual_admin_account_and_guards_list_and_kick(string scenario, bool allowed)
    {
        var context = new AuthControllerTestContext();
        var user = AuthControllerTestContext.User();
        user.IsAdmin = scenario is not ("RegularUser" or "ForgedAdminClaim");
        user.IsKicked = scenario == "SuspendedAdmin";
        user.DeletedAt = scenario == "DeletedAdmin" ? DateTime.UtcNow : null;
        if (scenario != "InvalidIdentity")
            context.Users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(scenario == "MissingAccount" ? null : user);
        var claims = new List<Claim> { new("sub", scenario == "InvalidIdentity" ? "invalid-id" : user.Id.ToString()) };
        if (scenario == "ForgedAdminClaim") claims.Add(new Claim("is_admin", "1"));
        var authorization = new AuthorizationHandlerContext([new AdminOnlyRequirement()],
            new ClaimsPrincipal(new ClaimsIdentity(claims, "UnitTest")), null);

        await new AdminOnlyHandler(context.Users.Object).HandleAsync(authorization);

        Assert.Equal(allowed, authorization.HasSucceeded);
        Assert.Equal(scenario is "SuspendedAdmin" or "DeletedAdmin", authorization.HasFailed);
        Assert.Contains(typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
            attribute => attribute.Policy == "AdminOnly");
        foreach (var action in new[] { nameof(AdminController.GetUsers), nameof(AdminController.KickUser) })
            Assert.DoesNotContain(typeof(AdminController).GetMethod(action)!.GetCustomAttributes(true), attribute => attribute is AllowAnonymousAttribute);
        if (scenario != "InvalidIdentity") context.Users.Verify(x => x.GetByIdAsync(user.Id, CancellationToken.None), Times.Once);
        context.AssertAllCallsVerified();
    }
}
