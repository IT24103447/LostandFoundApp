using Microsoft.AspNetCore.Authorization;

namespace AuthService.Authorization;

public class AdminOnlyRequirement : IAuthorizationRequirement { }
