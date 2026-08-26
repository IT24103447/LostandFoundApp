# RegisterForm.SeleniumTests — setup instructions

Selenium tests against your real registration form's UI — checking what the
**user sees** (inline errors, success messages, redirects), unlike the xUnit
tests which check what the **API returns**.

| Scenario | Covered by |
|---|---|
| 1 – Successful Registration | `Register_ValidData_ShowsSuccessMessage_AndNavigatesToVerifyEmail` |
| 2 – Duplicate Email | `Register_DuplicateEmail_ShowsInlineErrorOnEmailField` |
| 3 – Weak Password | `Register_WeakPassword_ShowsPasswordRequirementsError` |
| 4 – Invalid Phone Format | `Register_InvalidPhoneFormat_ShowsPhoneValidationError` |
| 5 – Required Field Validation | `Register_EmptyRequiredFields_ShowsInlineErrors_AndBlocksSubmission` |

## Before running

Unlike the xUnit project, these tests need the REAL app running (they drive an
actual browser against it):

1. Backend running: `dotnet run` in `services/AuthService` (MySQL + Kafka containers up)
2. Frontend running: `npm run dev` in `frontend/`
3. Google Chrome installed on this PC (any recent version — the test project
   downloads the matching driver automatically the first time you run it)

## Where to put this folder

Anywhere is fine since it's a standalone project — a sensible spot is next to
your other test project:

```
LostandFoundApp/
  services/
    AuthService/
    AuthService.Tests/
  RegisterForm.SeleniumTests/    <-- this folder
    RegisterForm.SeleniumTests.csproj
    ...
```

## Run it

From inside `RegisterForm.SeleniumTests`:

```
dotnet restore
dotnet test
```

A real Chrome window will pop open and drive itself through the form —
that's expected, watch it work. If you'd rather it ran invisibly, open
`BrowserFixture.cs` and uncomment the `--headless=new` line.

## Notes

- **Scenario 2 test creates two real users** in your database (one that
  succeeds, one duplicate attempt that's rejected) — this is expected and
  intentional, using randomly generated emails/phones so re-running the
  suite doesn't collide with earlier runs.
- If `Register_ValidData_...` fails claiming it navigated to `/login` instead
  of `/verify-email` — that's not a bug in the test, that's the AC-vs-code
  mismatch flagged separately (the story says "redirect to login," the actual
  code redirects to `/verify-email`). Update this test once that's resolved
  with dev.
