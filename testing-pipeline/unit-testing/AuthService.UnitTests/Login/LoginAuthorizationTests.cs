using System.Security.Claims;
using AuthService.Authorization;
using AuthService.Controllers;
using AuthService.Repositories;
using AuthService.UnitTests.Support;
using Microsoft.AspNetCore.Authorization;
using Moq;

namespace AuthService.UnitTests.Login;

public class LoginAuthorizationTests
{
    [Theory]
    [InlineData("ActiveAdmin", true)]
    [InlineData("RegularUser", false)]
    [InlineData("MissingUser", false)]
    [InlineData("SuspendedAdmin", false)]
    [InlineData("DeletedAdmin", false)]
    [InlineData("InvalidSubject", false)]
    [InlineData("MissingSubject", false)]
    public async Task Admin_policy_only_allows_existing_active_admin(string scenario, bool allowed)
    {
        var user = AuthControllerTestContext.User();
        user.IsAdmin = scenario != "RegularUser";
        user.IsKicked = scenario == "SuspendedAdmin";
        user.DeletedAt = scenario == "DeletedAdmin" ? DateTime.UtcNow : null;
        var users = new Mock<IUsersRepository>(MockBehavior.Strict);
        var invalidIdentity = scenario is "InvalidSubject" or "MissingSubject";
        if (!invalidIdentity)
            users.Setup(x => x.GetByIdAsync(user.Id, CancellationToken.None)).ReturnsAsync(scenario == "MissingUser" ? null : user);
        var claims = scenario == "MissingSubject" ? Array.Empty<Claim>()
            : new[] { new Claim("sub", scenario == "InvalidSubject" ? "invalid-id" : user.Id.ToString()) };
        var requirement = new AdminOnlyRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "UnitTest")), null);

        await new AdminOnlyHandler(users.Object).HandleAsync(context);

        Assert.Equal(allowed, context.HasSucceeded);
        Assert.Equal(scenario is "SuspendedAdmin" or "DeletedAdmin", context.HasFailed);
        Assert.Contains(typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
            attribute => attribute.Policy == "AdminOnly");
        if (!invalidIdentity) users.Verify(x => x.GetByIdAsync(user.Id, CancellationToken.None), Times.Once);
        users.VerifyNoOtherCalls();
    }
}
