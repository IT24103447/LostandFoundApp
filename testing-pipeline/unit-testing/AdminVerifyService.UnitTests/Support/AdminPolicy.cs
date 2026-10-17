using System.Reflection;
using System.Security.Claims;
using AdminVerifyService.Configuration;
using AdminVerifyService.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdminVerifyService.UnitTests.Support;

internal static class AdminPolicy
{
    public static async Task AssertAccessAsync(string action, bool authenticated, string? adminClaim, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = new string('x', 32), ["Jwt:Issuer"] = "unit-tests",
            ["Jwt:Audience"] = "unit-tests"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminSecurity(config);
        using var provider = services.BuildServiceProvider();
        var claims = adminClaim is null ? Array.Empty<Claim>() : [new Claim("is_admin", adminClaim)];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
        var controller = typeof(AdminSpamRecordsController);
        Assert.NotNull(controller.GetMethod(action));
        var policy = controller.GetCustomAttribute<AuthorizeAttribute>()!.Policy!;
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
        Assert.Equal(expected, result.Succeeded);
    }
}
