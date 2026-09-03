# AuthService.Tests — setup instructions

These are real unit tests written against your actual `AuthController`,
`PasswordValidator`, and `RegisterRequest` code (from the LostandFoundApp zip),
covering Scenarios 1–5 of the User Registration story:

| Scenario | Covered by |
|---|---|
| 1 – Successful Registration | `AuthControllerRegisterTests.Register_ValidData_...` |
| 2 – Duplicate Email / Phone | `AuthControllerRegisterTests.Register_Duplicate...` |
| 3 – Weak Password | `PasswordValidatorTests`, `AuthControllerRegisterTests.Register_WeakPassword_...` |
| 4 – Invalid Phone Format | `RegisterRequestValidationTests.PhoneNo_...` |
| 5 – Required Field Validation | `RegisterRequestValidationTests.*_Empty_FailsValidation` |

No database, SMTP, or Kafka connection is needed to run these — every dependency
of `AuthController` is mocked with Moq. These run in milliseconds.

## 1. Where to put this folder

Drop the `AuthService.Tests` folder so it sits **next to** `AuthService`, e.g.:

```
LostandFoundApp/
  services/
    AuthService/
      AuthService.csproj
    AuthService.Tests/       <-- this folder goes here
      AuthService.Tests.csproj
      ...
```

If your layout is different, open `AuthService.Tests.csproj` and fix the
`<ProjectReference Include="..\AuthService\AuthService.csproj" />` path to
point at the real `AuthService.csproj`.

## 2. Restore and run

From inside `AuthService.Tests`:

```
dotnet restore
dotnet test
```

First run downloads the test packages (xUnit, Moq, etc.) — same as any
`dotnet run`, this can take a minute the first time.

You should see output ending with something like:

```
Passed!  - Failed: 0, Passed: 20, Skipped: 0, Total: 20
```

## 3. If it doesn't compile

The most likely issue is a namespace mismatch — these tests assume your
project's root namespace is `AuthService` (matching `AuthService.Controllers`,
`AuthService.Services`, etc., as seen in your actual source files). If your
`AuthService.csproj` uses a different `<RootNamespace>`, update the `using`
statements at the top of each test file to match.

## 4. What's intentionally NOT covered here

- The MySQL duplicate-key race-condition fallback (`MySqlException` with
  `Number == 1062`) in `AuthController.Register` — this needs a real database
  to trigger safely and is a good candidate for an integration test instead.
- Anything involving a real HTTP call, real database, real SMTP send, or real
  Kafka — that's the next layer (integration tests via `WebApplicationFactory`),
  not unit tests. Happy to build that project next once this one is running
  green for you.

ci/cd trigger