using System.Reflection;
using System.Security.Claims;
using AuthService.Authorization;
using AuthService.Models;
using AuthService.Repositories;
using Microsoft.AspNetCore.Authorization;
using Moq;

namespace AdminVerifyService.UnitTests.AdminListingAndUserAccess;

public sealed class AdminAccessPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task UserLookupPolicyRequiresAnActiveAdminAccount(bool admin, bool kicked, bool allowed)
    {
        var user = new User { Id = Guid.NewGuid(), IsAdmin = admin, IsKicked = kicked };
        var users = new Mock<IUsersRepository>(MockBehavior.Strict);
        users.Setup(x => x.GetByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));
        var requirement = new AdminOnlyRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await new AdminOnlyHandler(users.Object).HandleAsync(context);

        Assert.Equal(allowed, context.HasSucceeded);
        Assert.Equal("AdminOnly", typeof(AuthService.Controllers.AdminController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void ItemAdminReadDeleteAndPhotosAllCarryTheAdminOnlyPolicy()
    {
        var controller = typeof(ItemService.Controllers.AdminItemsController);
        Assert.Equal("AdminOnly", controller.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.NotNull(controller.GetMethod("GetItem"));
        Assert.NotNull(controller.GetMethod("GetItemPhotos"));
        Assert.NotNull(controller.GetMethod("DeleteItem"));
    }
}
