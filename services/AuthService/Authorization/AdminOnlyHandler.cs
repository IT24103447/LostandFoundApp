using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthService.Repositories;
using Microsoft.AspNetCore.Authorization;

namespace AuthService.Authorization;

public class AdminOnlyHandler : AuthorizationHandler<AdminOnlyRequirement>
{
    private readonly IUsersRepository _users;

    public AdminOnlyHandler(IUsersRepository users)
    {
        _users = users;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminOnlyRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return;
        }

        var user = await _users.GetByIdAsync(userId);
        if (user is null || !user.IsAdmin)
        {
            return;
        }

        if (user.IsKicked || user.DeletedAt is not null)
        {
            context.Fail(new AuthorizationFailureReason(this, "Account not active."));
            return;
        }

        context.Succeed(requirement);
    }
}
