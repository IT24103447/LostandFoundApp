using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Repositories;
using Microsoft.AspNetCore.Authorization;

namespace AuthService.Authorization;

public class ActiveUserHandler : AuthorizationHandler<ActiveUserRequirement>
{
    private readonly IUsersRepository _users;

    public ActiveUserHandler(IUsersRepository users)
    {
        _users = users;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveUserRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return;
        }

        var user = await _users.GetByIdAsync(userId);
        if (user is null)
        {
            return;
        }

        if (user.IsKicked)
        {
            context.Fail(new AuthorizationFailureReason(this, "Account suspended."));
            return;
        }

        context.Succeed(requirement);
    }
}
