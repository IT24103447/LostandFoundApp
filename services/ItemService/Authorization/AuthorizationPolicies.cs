namespace ItemService.Authorization;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string IsAdminClaim = "is_admin";
    public const string IsAdminValue = "1";
}
