# Assessment unit testing

Tools: .NET 8, xUnit and Moq. These tests run the real backend controller, DTO validation, JWT generation, session validation and authorization handler while isolating repositories, password hashing, email, OTP generation and event publication. No database, Kafka, SMTP, deployed service or browser is required.

## Structure and coverage

One service project, one folder/namespace per story. `Registration` is the existing reference. `Support/AuthControllerTestContext.cs` provides fresh strict mocks per test; it contains no discoverable tests and is not a story.

| Story | Cases | Coverage |
|---|---|---|
| Registration | 18 | Creation, duplicate email/phone, password complexity/boundaries, required fields and phone validation |
| EmailVerification | 39 | Success and pending-email changes, consumed-code retry contract, wrong-code counts and lockout, missing active OTP, invalid sessions/account states, resend replacement/order/expiry/cooldown, DTO validation and real session signature/lifetime validation |
| Login | 20 | User/admin responses, development/production cookies, generic credential rejection, repository-filtered deleted accounts, unverified/suspended account behaviour, real signed JWT claims/lifetime and AdminOnly authorization decisions |
| Profile | 29 | Caller-owned reads/writes, name/phone/both updates, reload and exact changed-field events, unchanged input, duplicate phones, invalid identity/missing account, view restrictions and DTO validation boundaries |

Validation on 13 October 2026: Release build succeeded with zero warnings/errors; all 106 cases passed with none skipped. The prepared full assessment unit runner took 5 seconds including reporting on the current workstation. This measures the current Auth suite, not future service projects.

`[Theory]` and `[InlineData]` express meaningful input/state partitions instead of repeated test bodies. `[Fact]` expresses individual behaviours. Strict mocks fail on unconfigured calls, and verification checks expected interactions and unexpected calls. `MockSequence` checks resend invalidation precedes code creation/email and profile saves precede reload/event publication. A distinct cancellation token verifies forwarding on successful login and profile update.

## Match the current implementation

As requested, these tests follow the service code where the stories differ:

- Verification sets the database flag and consumes the OTP, then issues a JWT/auth cookie. It does not log the device out.
- Current OTP settings are ten attempts, ten-minute expiry and sixty-second resend cooldown. A wrong tenth attempt consumes the code; the first and ninth wrong attempts do not.
- Unverified login returns 403 and a verification session plus a new emailed OTP, without a login JWT.
- Admin role is the `is_admin` claim (`1`/`0`), not a conventional role string. Verification is `email_verified` (`1`/`0`).
- Profile `/me` selects the account from `sub` or `NameIdentifier`; its request has no target user ID. Tests check every read/write stays on the caller ID. Current names accept lengths 1–150, with empty/whitespace values rejected.

Scope limits to explain in the VIVA:

- Mocked repositories test the controller's dependency contract, not MySQL persistence or SQL expiry/used/deleted filtering. The consumed-code retry test explicitly stubs the repository's inactive-code response.
- DTO tests run real DataAnnotations validation. Direct controller calls do not run MVC automatic model validation, so these tests do not claim to prove HTTP rejection/no persistence for malformed DTOs.
- Handler tests prove authorization decisions and the AdminController policy attribute; middleware-generated HTTP 403 responses, frontend messages/redirects and ItemService posting restrictions require integration/UI or that service's own tests.
- Verification currently issues its immediate JWT from the originally loaded user without updating its in-memory `IsEmailVerified` flag. The success test checks the verification write and issuance call, and does not claim the immediate token contains a true verification claim. JWT tests prove a freshly loaded verified user's token has `email_verified=1`.

Production files and the original registration tests are unchanged.

## Run from VS Code on Windows

Open `LostandFoundApp` in VS Code and select **Terminal > New Terminal**, using PowerShell. Start from the repository root:

```powershell
Set-Location 'C:\Users\minud\OneDrive\Documents\ASE\LostandFoundApp'
.\test-unit.ps1 -Prepare
.\test-unit.ps1 -NoBuild
```

`-Prepare` restores and builds all available assessment unit projects once. `-NoBuild` runs the prepared tests and reports without restore/build delays. After editing any test/service source, prepare again before using `-NoBuild`. A regular `.\test-unit.ps1` restores/builds and runs in one invocation.

Select a service or story:

```powershell
.\test-unit.ps1 -Service AuthService -NoBuild
.\test-unit.ps1 -Service AuthService -Story EmailVerification -NoBuild
.\test-unit.ps1 -Service AuthService -Story Login -NoBuild
.\test-unit.ps1 -Service AuthService -Story Profile -NoBuild
.\test-unit.ps1 -Service AuthService -Story Registration -NoBuild
```

`-Story` requires `-Service`. To prepare one service, use `.\test-unit.ps1 -Service AuthService -Prepare`.

The helper requires Git for Windows (Git Bash), Node.js and the .NET 8 SDK. It detects dotnet on PATH, the usual installed location, or the temporary SDK previously used for this workstation's validation. You can explicitly choose an existing SDK:

```powershell
.\test-unit.ps1 -Prepare -DotNetPath 'C:\path\to\dotnet.exe'
.\test-unit.ps1 -NoBuild -DotNetPath 'C:\path\to\dotnet.exe'
```

It never downloads tools or changes your permanent PATH. If PowerShell blocks script execution, allow scripts only in the current terminal, then retry:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

Build outputs for tests and referenced services stay inside `testing-pipeline/unit-testing/TestResults/build`. The latest run report is `testing-pipeline/unit-testing/TestResults/latest/unit-testing/summary.md`, alongside `details.json`, `console.log`, `status.txt` and `results/*.trx`. Earlier reports move into `TestResults/history`.

The PowerShell helper uses a separate output layout from direct `dotnet`/Bash runs below; use its own `-Prepare` before its `-NoBuild`.

## Direct Bash / CI commands

From the repository root, with the SDK selected by `global.json` and Node.js available:

```bash
dotnet restore testing-pipeline/unit-testing/AuthService.UnitTests/AuthService.UnitTests.csproj
dotnet build testing-pipeline/unit-testing/AuthService.UnitTests/AuthService.UnitTests.csproj -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false
```

Build each future service test project ahead of the demo too. On Windows use PowerShell for these two commands, and Git Bash/WSL with the corresponding installed SDK for the report runner. Windows and WSL need their own restore/build outputs; prepare and run in the same environment.

Run all available assessment service projects, with a fresh output directory each time:

```bash
export ASE_REPORT_ROOT="$(mktemp -d -t ase-unit-reports.XXXXXX)"
bash testing-pipeline/unit-testing/run.sh --no-build --no-restore
```

The runner prints service/story groups, each outcome, counts and total wall-clock seconds. Inspect `unit-testing/summary.md`, `details.json`, `console.log` and native `results/*.trx` under `ASE_REPORT_ROOT`. The existing GitHub Actions unit job runs this same runner and uploads reports; CI should retain its normal restore/build.

For a focused explanation, choose one story (use a new report directory for each invocation):

```bash
export ASE_REPORT_ROOT="$(mktemp -d -t ase-unit-reports.XXXXXX)"
bash testing-pipeline/unit-testing/run.sh --service AuthService --story EmailVerification --no-build --no-restore
```

Use `Login`, `Profile` or `Registration` as the story name. A direct PowerShell run without Node/Bash reporting is also possible:

```powershell
dotnet test testing-pipeline/unit-testing/AuthService.UnitTests/AuthService.UnitTests.csproj -c Release --no-build --no-restore
```

No tests sleep through cooldown/expiry or perform real password hashing/network calls. xUnit retains its default parallelism across test classes, with independent state. Measure the **whole runner**, including test-process startup and reporting, as other services are added; summed case durations do not represent elapsed suite time. Aim to leave time within the two-minute slot to explain one parameterized boundary case, one mock interaction and the final report. Timing of the future five-service suite must be measured when it exists.

## Contribution and VIVA notes

Unit-testing owner: name/student ID to be supplied before submission.

Explain that stubs return controlled dependency data, mocks also verify interactions, parameterized tests cover partitions and boundaries, and strict isolation makes the suite deterministic and fast. A passing mocked test does not prove the database, email delivery or browser flow works. Use the report's descriptions to show the precise assertion scope.
