# LoginForm.SeleniumTests

Selenium + xUnit tests for the **User and Admin Login** story in the real Lost & Found frontend.

## What this suite covers

| Acceptance criterion / check | Test |
|---|---|
| Scenario 1 - Successful User Login | `UserLogin_ValidVerifiedUser_RedirectsToUserDashboard_AndSetsJwtCookie` |
| Scenario 2 - Successful Admin Login | `AdminLogin_ValidAdmin_RedirectsToAdminDashboard_AndJwtContainsAdminClaim` |
| Scenario 3 - Invalid Credentials | `Login_InvalidCredentials_ShowsSameGenericError` |
| Scenario 4 - Deleted Account | `Login_DeletedAccount_IsRejected_WithNonRevealingError` |
| Scenario 5 - Unverified Account | `UnverifiedUserLogin_AcRequiresAuthenticatedUnverifiedSession` |
| Scenario 6 - Frontend admin route guard | `RegularUser_AdminFrontendRoute_IsDeniedByClientRouteGuard` |
| Scenario 6 - Backend admin API must return 403 | `RegularUser_AdminApiRequest_MustReturn403` |
| Additional login form validation | `Login_EmptyRequiredFields_ShowsInlineValidationErrors_AndDoesNotSubmit` |

## Why some tests are expected to expose defects

The code was inspected before writing this suite. Two story/code mismatches are currently present:

1. **Scenario 5 mismatch:** the story says an unverified user can log in with an unverified session/JWT. The current `AuthController.Login` returns HTTP 403 with a verification session token and no `auth_token` cookie. `LoginForm.tsx` therefore sends the user to `/verify-email`. The Selenium test intentionally asserts the story requirement, so it should fail against the current implementation until the AC or code is reconciled.

2. **Scenario 6 backend authorization gap:** the frontend `ProtectedRoute` does prevent a normal user from rendering `/admin/*` and redirects them to `/`. However, `AdminController` is protected only by `ActiveUser`, and the current backend policy does not check `IsAdmin`. The API-level Selenium test intentionally expects HTTP 403 and therefore exposes the current authorization defect.

The existing backend unit test `AdminRouteAuthorizationTests` confirms this gap.

## Project prerequisites

Start the application first:

```text
Auth service:  http://localhost:5261
Frontend:      http://localhost:5173
```

The auth service must be running in **Development** so the seeded users exist.

The project already seeds these verified accounts:

```text
Regular user
Email:    user1@example.com
Password: User123!
Name:     User One

Admin
Email:    admin1@lostandfound.com
Password: Admin123!
Name:     Admin One
```

## Deleted-account test data

Scenario 4 needs an account that is already soft-deleted. Do not use one of the seeded accounts unless you intentionally want to mutate your development database.

Set these environment variables to a dedicated deleted test account:

### PowerShell

```powershell
$env:SELENIUM_DELETED_USER_EMAIL="deleted.selenium@example.com"
$env:SELENIUM_DELETED_USER_PASSWORD="SomePassword1!"
```

The account must exist with `deleted_at IS NOT NULL` in the auth-service database.

## Run

From this folder:

```powershell
dotnet restore
dotnet test
```

To watch Chrome, leave the fixture as-is. For headless execution, uncomment:

```csharp
options.AddArgument("--headless=new");
```

## Suggested QA interpretation

A successful run of the suite does **not** mean the story is fully accepted if the two known-defect tests are still failing. Those failures are useful QA evidence:

```text
Scenario 5 -> implementation does not satisfy the written AC
Scenario 6 backend -> regular-user JWT is not rejected with 403 by AdminController
```

The frontend route-guard test can still pass while the backend API authorization test fails; both layers matter for a microservice architecture.
