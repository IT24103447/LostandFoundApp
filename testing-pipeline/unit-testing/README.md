# Unit tests

**523 cases | 4 services | 32 stories**

Latest full run, 18 October 2026: **523 passed, 0 failed, 0 skipped**.

| Service | Stories | Cases | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|---:|
| [Auth](#authservice-catalogue) | 7 | 176 | 176 | 0 | 0 |
| [Item](#itemservice-catalogue) | 8 | 115 | 115 | 0 | 0 |
| [Matching](#matchingservice-catalogue) | 10 | 136 | 136 | 0 | 0 |
| [Admin Verify](#adminverifyservice-catalogue) | 7 | 96 | 96 | 0 | 0 |
| **Total** | **32** | **523** | **523** | **0** | **0** |

[Setup](#setup) · [Run one service](#run-one-service) · [Run one story](#run-one-story) · [Demo mode](#demo-mode) · [Test catalogue](#test-catalogue)

## Setup

1. **Install:** [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (8.0.424 or a compatible later .NET 8 SDK), [Node.js](https://nodejs.org/en/download) (22.14+ or 24.x), [Git for Windows](https://git-scm.com/install/windows) with Git Bash.
2. **Open the complete `LostandFoundApp` folder in VS Code.** Open a **PowerShell** terminal at the repository root, where `test-unit.ps1` is located. Reopen VS Code after installing tools.
3. **Run these commands in order:**

```powershell
# Check tools
dotnet --version
node --version
git --version

# Prepare once before running
.\test-unit.ps1 -Prepare

# Run the entire suite
.\test-unit.ps1 -NoBuild

# Open the report, then press Ctrl+Shift+V to preview
code .\testing-pipeline\unit-testing\TestResults\latest\unit-testing\summary.md
```

**No APIs, database, Kafka, frontend server or containers need to run.** Internet is needed for uncached NuGet packages during preparation.

**After source changes:** repeat `-Prepare`. **If scripts are blocked:** run `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`, then retry.

## Run one service

After preparation, choose one:

```powershell
.\test-unit.ps1 -Service AuthService -NoBuild
.\test-unit.ps1 -Service ItemService -NoBuild
.\test-unit.ps1 -Service MatchingService -NoBuild
.\test-unit.ps1 -Service AdminVerifyService -NoBuild
```

## Run one story

```powershell
.\test-unit.ps1 -Service AuthService -Story Registration -NoBuild
```

Every story below includes its exact command. Run all commands from the repository root.

## Demo mode

After `-Prepare`, add `-Demo` for service, story and individual-case output without indentation:

```powershell
# Entire suite: adaptive review pauses after services, targeting 110 seconds
.\test-unit.ps1 -NoBuild -Demo

# One service or one story: detailed output without service-end pauses
.\test-unit.ps1 -Service AuthService -NoBuild -Demo
.\test-unit.ps1 -Service AuthService -Story Registration -NoBuild -Demo
```

Stories execute sequentially in one terminal. Slow execution reduces or removes pauses; tests are never cut short at 110 seconds. Execution time and presentation delays are reported separately. Omit `-Demo` for fast mode.

**Results:** `TestResults/latest/unit-testing/` contains `summary.md`, `details.json`, `console.log`, `status.txt` and `results/*.trx`. Previous runs move to `TestResults/history/`. These paths are inside `testing-pipeline/unit-testing/`. Success returns exit code `0`; check with `$LASTEXITCODE`.

## Test catalogue

Expand a story for its individual cases. Each parameter row counts as one case. Source links show the exact assertions; external dependencies are mocked or stubbed.

<a id="authservice-catalogue"></a>

## Auth Service (176 cases)

| Story | Cases | Tested scope |
|---|---:|---|
| [Registration](#authservice-registration) | 18 | Account creation, duplicate contact checks, password rules and registration DTO validation. |
| [Email Verification](#authservice-emailverification) | 39 | OTP verification, attempt limits, resend cooldowns and verification-session validation. |
| [Login](#authservice-login) | 20 | Credential handling, returned identity and role, JWT validation and admin authorization policy. |
| [Profile](#authservice-profile) | 29 | Caller-scoped profile reads and updates, changed-field events and name/phone validation. |
| [Password Reset](#authservice-passwordreset) | 34 | Reset-code requests, password rules, attempt limits, code consumption and subsequent login behaviour. |
| [Account Deletion](#authservice-accountdeletion) | 14 | Password confirmation, token cleanup, soft deletion, deletion events and subsequent login rejection. |
| [Admin User Management](#authservice-adminusermanagement) | 22 | User-list projections, repository query arguments, pagination, admin policy and kick behaviour. |

<a id="authservice-registration"></a>

### Registration (18 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story Registration -NoBuild`

<details><summary>All 18 individual cases in Registration (7 checks)</summary>

#### Duplicate email rejects registration without side effects

[RegistrationControllerTests.cs:94](AuthService.UnitTests/Registration/RegistrationControllerTests.cs#L94)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-001 | <code>No parameters; the test body defines the fixture.</code> | Rejects an existing email before checking phone or creating a user; verifies hashing, OTP, email and session dependencies are untouched. |

#### Duplicate phone rejects registration without side effects

[RegistrationControllerTests.cs:108](AuthService.UnitTests/Registration/RegistrationControllerTests.cs#L108)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-002 | <code>No parameters; the test body defines the fixture.</code> | Rejects an existing phone number after the email check without creating a user, hashing a password or sending email. |

#### Valid registration creates unverified user and requests verification email

[RegistrationControllerTests.cs:42](AuthService.UnitTests/Registration/RegistrationControllerTests.cs#L42)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-003 | <code>No parameters; the test body defines the fixture.</code> | Creates an unverified non-admin user using the returned password hash; stores a hashed OTP with expiry and requests verification email/session creation through mocks. |

#### Weak password returns requirements before repository access

[RegistrationControllerTests.cs:122](AuthService.UnitTests/Registration/RegistrationControllerTests.cs#L122)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-004 | <code>No parameters; the test body defines the fixture.</code> | Returns password requirements for weak input before accessing repositories or performing registration side effects. |

#### Password rules reject invalid inputs and accept length boundaries

[RegistrationPasswordTests.cs:15](AuthService.UnitTests/Registration/RegistrationPasswordTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-005 | <code>scenario: "MaximumLength", expectedValid: True, expectedError: null</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-006 | <code>scenario: "MinimumLength", expectedValid: True, expectedError: null</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-007 | <code>scenario: "NoDigit", expectedValid: False, expectedError: "Password must contain at least one digit."</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-008 | <code>scenario: "NoLowercase", expectedValid: False, expectedError: "Password must contain at least one lowercase lette"···</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-009 | <code>scenario: "NoUppercase", expectedValid: False, expectedError: "Password must contain at least one uppercase lette"···</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-010 | <code>scenario: "TooLong", expectedValid: False, expectedError: "Password must be at most 128 characters long."</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |
| UT-1-01-011 | <code>scenario: "TooShort", expectedValid: False, expectedError: "Password must be at least 8 characters long."</code> | Checks the real password validator for the named complexity rule or the minimum/maximum permitted length. |

#### Each required field rejects empty input

[RegistrationValidationTests.cs:35](AuthService.UnitTests/Registration/RegistrationValidationTests.cs#L35)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-012 | <code>field: "Email"</code> | Checks the real registration DTO reports a required-field error when the named field is empty. |
| UT-1-01-013 | <code>field: "Name"</code> | Checks the real registration DTO reports a required-field error when the named field is empty. |
| UT-1-01-014 | <code>field: "Password"</code> | Checks the real registration DTO reports a required-field error when the named field is empty. |
| UT-1-01-015 | <code>field: "PhoneNo"</code> | Checks the real registration DTO reports a required-field error when the named field is empty. |

#### Invalid phone formats produce phone validation error

[RegistrationValidationTests.cs:18](AuthService.UnitTests/Registration/RegistrationValidationTests.cs#L18)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-01-016 | <code>phone: "+0771234567"</code> | Checks the real registration DTO rejects the supplied invalid phone format and identifies the phone field. |
| UT-1-01-017 | <code>phone: "+94"</code> | Checks the real registration DTO rejects the supplied invalid phone format and identifies the phone field. |
| UT-1-01-018 | <code>phone: "0771234567"</code> | Checks the real registration DTO rejects the supplied invalid phone format and identifies the phone field. |

</details>

<a id="authservice-emailverification"></a>

### Email Verification (39 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story EmailVerification -NoBuild`

<details><summary>All 39 individual cases in Email Verification (14 checks)</summary>

#### Consumed otp cannot verify again when repository returns no active code

[EmailVerificationControllerTests.cs:68](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L68)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-001 | <code>No parameters; the test body defines the fixture.</code> | After consumption, stubs the repository returning no active code; a retry cannot repeat verification, publication or JWT issuance. Does not test SQL filtering. |

#### Correct otp marks account verified consumes code and issues auth cookie

[EmailVerificationControllerTests.cs:32](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L32)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-002 | <code>production: False, changedEmail: False</code> | Verifies account/code updates, verification event payload, optional email change, returned token and environment-specific auth cookie using strict mocks. |
| UT-1-02-003 | <code>production: False, changedEmail: True</code> | Verifies account/code updates, verification event payload, optional email change, returned token and environment-specific auth cookie using strict mocks. |
| UT-1-02-004 | <code>production: True, changedEmail: False</code> | Verifies account/code updates, verification event payload, optional email change, returned token and environment-specific auth cookie using strict mocks. |

#### Incorrect otp increments attempts and locks at configured limit

[EmailVerificationControllerTests.cs:96](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L96)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-005 | <code>previousAttempts: 0, locked: False</code> | Checks retry and lockout boundaries with a ten-attempt configuration; wrong codes increment once and never verify the account. |
| UT-1-02-006 | <code>previousAttempts: 8, locked: False</code> | Checks retry and lockout boundaries with a ten-attempt configuration; wrong codes increment once and never verify the account. |
| UT-1-02-007 | <code>previousAttempts: 9, locked: True</code> | Checks retry and lockout boundaries with a ten-attempt configuration; wrong codes increment once and never verify the account. |

#### Invalid verification session rejects before repository access

[EmailVerificationControllerTests.cs:133](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L133)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-008 | <code>No parameters; the test body defines the fixture.</code> | An invalid verification session rejects before accessing account/code repositories or issuing authentication. |

#### No active otp returns invalid or expired error without verification

[EmailVerificationControllerTests.cs:117](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L117)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-009 | <code>No parameters; the test body defines the fixture.</code> | Stubs the repository returning no active OTP and checks the error with no verification side effects; expiry filtering itself belongs to integration tests. |

#### Pending email claimed by another account consumes code without verifying

[EmailVerificationControllerTests.cs:161](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L161)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-010 | <code>No parameters; the test body defines the fixture.</code> | An email claimed since OTP issuance invalidates the code without changing or verifying the account. |

#### Verified or suspended account rejects before otp access

[EmailVerificationControllerTests.cs:146](AuthService.UnitTests/EmailVerification/EmailVerificationControllerTests.cs#L146)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-011 | <code>verified: False, suspended: True, status: 403, error: "Your account has been suspended."</code> | Already-verified or suspended accounts return their current errors without processing an OTP. |
| UT-1-02-012 | <code>verified: True, suspended: False, status: 400, error: "Email is already verified."</code> | Already-verified or suspended accounts return their current errors without processing an OTP. |

#### Otp validation requires exactly six digits

[EmailVerificationValidationTests.cs:15](AuthService.UnitTests/EmailVerification/EmailVerificationValidationTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-013 | <code>code: "", valid: False</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |
| UT-1-02-014 | <code>code: "000001", valid: True</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |
| UT-1-02-015 | <code>code: "12345", valid: False</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |
| UT-1-02-016 | <code>code: "123456", valid: True</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |
| UT-1-02-017 | <code>code: "1234567", valid: False</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |
| UT-1-02-018 | <code>code: "12a456", valid: False</code> | Exercises real DTO validation for missing, malformed and valid six-digit OTPs, including leading zeroes. |

#### Resend validation requires session and valid email

[EmailVerificationValidationTests.cs:41](AuthService.UnitTests/EmailVerification/EmailVerificationValidationTests.cs#L41)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-019 | <code>session: "", email: "story@example.test", field: "SessionToken"</code> | Checks resend DTO validation identifies the missing session or missing/malformed email. |
| UT-1-02-020 | <code>session: "session", email: "", field: "Email"</code> | Checks resend DTO validation identifies the missing session or missing/malformed email. |
| UT-1-02-021 | <code>session: "session", email: "not-an-email", field: "Email"</code> | Checks resend DTO validation identifies the missing session or missing/malformed email. |

#### Verification requires session token

[EmailVerificationValidationTests.cs:28](AuthService.UnitTests/EmailVerification/EmailVerificationValidationTests.cs#L28)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-022 | <code>No parameters; the test body defines the fixture.</code> | Checks that verification DTO validation identifies a missing session token. |

#### Allowed resend invalidates old codes stores hash and emails new code

[ResendOtpTests.cs:16](AuthService.UnitTests/EmailVerification/ResendOtpTests.cs#L16)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-023 | <code>scenario: "ChangedEmail", changedEmail: True</code> | Checks invalidation before generation, hashed OTP storage, configured expiry, email content and resend timestamp; includes cooldown elapsed and changed email. |
| UT-1-02-024 | <code>scenario: "CooldownElapsed", changedEmail: False</code> | Checks invalidation before generation, hashed OTP storage, configured expiry, email content and resend timestamp; includes cooldown elapsed and changed email. |
| UT-1-02-025 | <code>scenario: "FirstResend", changedEmail: False</code> | Checks invalidation before generation, hashed OTP storage, configured expiry, email content and resend timestamp; includes cooldown elapsed and changed email. |

#### Invalid resend rejects without creating or sending code

[ResendOtpTests.cs:81](AuthService.UnitTests/EmailVerification/ResendOtpTests.cs#L81)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-026 | <code>scenario: "DuplicateEmail", status: 400, error: "This email is already registered by another accoun"···</code> | Invalid sessions, missing/suspended accounts or duplicate pending emails reject without OTP/email side effects. |
| UT-1-02-027 | <code>scenario: "InvalidSession", status: 400, error: "Invalid or expired verification session."</code> | Invalid sessions, missing/suspended accounts or duplicate pending emails reject without OTP/email side effects. |
| UT-1-02-028 | <code>scenario: "MissingUser", status: 400, error: "Invalid or expired verification session."</code> | Invalid sessions, missing/suspended accounts or duplicate pending emails reject without OTP/email side effects. |
| UT-1-02-029 | <code>scenario: "SuspendedUser", status: 403, error: "Your account has been suspended."</code> | Invalid sessions, missing/suspended accounts or duplicate pending emails reject without OTP/email side effects. |

#### Resend inside cooldown returns retry after without side effects

[ResendOtpTests.cs:57](AuthService.UnitTests/EmailVerification/ResendOtpTests.cs#L57)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-030 | <code>elapsedSeconds: 0</code> | Checks HTTP 429 and Retry-After for recent resend timestamps, with no new OTP or email and no real waiting. |
| UT-1-02-031 | <code>elapsedSeconds: 30</code> | Checks HTTP 429 and Retry-After for recent resend timestamps, with no new OTP or email and no real waiting. |

#### Verification session validates signature identity and lifetime

[VerificationSessionTests.cs:23](AuthService.UnitTests/EmailVerification/VerificationSessionTests.cs#L23)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-02-032 | <code>scenario: "Empty", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-033 | <code>scenario: "Expired", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-034 | <code>scenario: "InvalidSubject", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-035 | <code>scenario: "Malformed", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-036 | <code>scenario: "Valid", valid: True</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-037 | <code>scenario: "WrongAudience", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-038 | <code>scenario: "WrongIssuer", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |
| UT-1-02-039 | <code>scenario: "WrongSignature", valid: False</code> | Exercises the real session validator with valid, expired, wrong-key/issuer/audience, invalid-subject and malformed tokens; no sleep or network. |

</details>

<a id="authservice-login"></a>

### Login (20 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story Login -NoBuild`

<details><summary>All 20 individual cases in Login (7 checks)</summary>

#### Admin policy only allows existing active admin

[LoginAuthorizationTests.cs:21](AuthService.UnitTests/Login/LoginAuthorizationTests.cs#L21)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-001 | <code>scenario: "ActiveAdmin", allowed: True</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-002 | <code>scenario: "DeletedAdmin", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-003 | <code>scenario: "InvalidSubject", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-004 | <code>scenario: "MissingSubject", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-005 | <code>scenario: "MissingUser", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-006 | <code>scenario: "RegularUser", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |
| UT-1-03-007 | <code>scenario: "SuspendedAdmin", allowed: False</code> | Runs the real AdminOnly handler against account/identity cases and checks AdminController uses that policy; HTTP middleware is outside this unit test. |

#### Invalid credentials or filtered deleted account return same error without token

[LoginControllerTests.cs:57](AuthService.UnitTests/Login/LoginControllerTests.cs#L57)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-008 | <code>scenario: "DeletedAccount"</code> | Unknown email, incorrect password and repository-filtered deleted account return the same generic error with no token/cookie. |
| UT-1-03-009 | <code>scenario: "UnknownEmail"</code> | Unknown email, incorrect password and repository-filtered deleted account return the same generic error with no token/cookie. |
| UT-1-03-010 | <code>scenario: "WrongPassword"</code> | Unknown email, incorrect password and repository-filtered deleted account return the same generic error with no token/cookie. |

#### Suspended verified account rejects login without token

[LoginControllerTests.cs:110](AuthService.UnitTests/Login/LoginControllerTests.cs#L110)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-011 | <code>No parameters; the test body defines the fixture.</code> | Correct credentials on a suspended verified account return HTTP 403 without issuing authentication. |

#### Unverified login returns forbidden and sends new verification code without login jwt

[LoginControllerTests.cs:76](AuthService.UnitTests/Login/LoginControllerTests.cs#L76)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-012 | <code>No parameters; the test body defines the fixture.</code> | Documents current behaviour: HTTP 403 plus verification session, old-code invalidation and emailed replacement OTP, without a login JWT. |

#### Valid credentials return user or admin details and auth cookie

[LoginControllerTests.cs:19](AuthService.UnitTests/Login/LoginControllerTests.cs#L19)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-013 | <code>admin: False, production: False</code> | Checks password verification against the stored hash, exact returned profile/role, JWT issuance, cookie security options and cancellation propagation. |
| UT-1-03-014 | <code>admin: False, production: True</code> | Checks password verification against the stored hash, exact returned profile/role, JWT issuance, cookie security options and cancellation propagation. |
| UT-1-03-015 | <code>admin: True, production: False</code> | Checks password verification against the stored hash, exact returned profile/role, JWT issuance, cookie security options and cancellation propagation. |
| UT-1-03-016 | <code>admin: True, production: True</code> | Checks password verification against the stored hash, exact returned profile/role, JWT issuance, cookie security options and cancellation propagation. |

#### Login jwt is rejected when validated with wrong signing key

[LoginJwtTests.cs:58](AuthService.UnitTests/Login/LoginJwtTests.cs#L58)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-017 | <code>No parameters; the test body defines the fixture.</code> | A real issued login JWT fails cryptographic validation with a different signing key. |

#### Signed login jwt has correct identity role verification and expiry

[LoginJwtTests.cs:34](AuthService.UnitTests/Login/LoginJwtTests.cs#L34)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-03-018 | <code>admin: False, verified: False</code> | Validates a real HS256 signature, issuer, audience and lifetime, then checks identity, contact, admin and verification claims. |
| UT-1-03-019 | <code>admin: False, verified: True</code> | Validates a real HS256 signature, issuer, audience and lifetime, then checks identity, contact, admin and verification claims. |
| UT-1-03-020 | <code>admin: True, verified: True</code> | Validates a real HS256 signature, issuer, audience and lifetime, then checks identity, contact, admin and verification claims. |

</details>

<a id="authservice-profile"></a>

### Profile (29 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story Profile -NoBuild`

<details><summary>All 29 individual cases in Profile (8 checks)</summary>

#### Duplicate profile phone rejects without saving or publishing

[ProfileControllerTests.cs:83](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L83)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-001 | <code>No parameters; the test body defines the fixture.</code> | A phone belonging to another user returns HTTP 409 without profile persistence or event publication. |

#### Ineligible account cannot view profile

[ProfileControllerTests.cs:134](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L134)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-002 | <code>suspended: False, verified: False, status: 401, error: "Email not verified."</code> | Suspended or unverified accounts receive the current profile-view rejection without token issuance. |
| UT-1-04-003 | <code>suspended: True, verified: False, status: 403, error: "Your account has been suspended."</code> | Suspended or unverified accounts receive the current profile-view rejection without token issuance. |

#### Missing or invalid profile identity rejects before repository access

[ProfileControllerTests.cs:102](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L102)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-004 | <code>action: "Update", subject: "invalid-id"</code> | Both view and update reject missing/invalid caller identity before repository access or writes. |
| UT-1-04-005 | <code>action: "Update", subject: null</code> | Both view and update reject missing/invalid caller identity before repository access or writes. |
| UT-1-04-006 | <code>action: "View", subject: "invalid-id"</code> | Both view and update reject missing/invalid caller identity before repository access or writes. |
| UT-1-04-007 | <code>action: "View", subject: null</code> | Both view and update reject missing/invalid caller identity before repository access or writes. |

#### Missing profile account rejects without saving

[ProfileControllerTests.cs:117](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L117)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-008 | <code>action: "Update"</code> | Both view and update return HTTP 401 for a caller account absent from the repository, without writes. |
| UT-1-04-009 | <code>action: "View"</code> | Both view and update return HTTP 401 for a caller account absent from the repository, without writes. |

#### Profile update persists callers record returns reload and publishes only changed fields

[ProfileControllerTests.cs:39](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L39)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-010 | <code>scenario: "Both", claimType: "http://schemas.xmlsoap.org/ws/2005/05/identity/cla"···</code> | Checks caller-owned lookup, uniqueness check, save/reload order and exact changed-field event payload for name, phone, both or unchanged input. |
| UT-1-04-011 | <code>scenario: "NameOnly", claimType: "sub"</code> | Checks caller-owned lookup, uniqueness check, save/reload order and exact changed-field event payload for name, phone, both or unchanged input. |
| UT-1-04-012 | <code>scenario: "PhoneOnly", claimType: "sub"</code> | Checks caller-owned lookup, uniqueness check, save/reload order and exact changed-field event payload for name, phone, both or unchanged input. |
| UT-1-04-013 | <code>scenario: "Unchanged", claimType: "sub"</code> | Checks caller-owned lookup, uniqueness check, save/reload order and exact changed-field event payload for name, phone, both or unchanged input. |

#### View profile uses authenticated identity and returns contact details

[ProfileControllerTests.cs:18](AuthService.UnitTests/Profile/ProfileControllerTests.cs#L18)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-014 | <code>claimType: "http://schemas.xmlsoap.org/ws/2005/05/identity/cla"···</code> | Checks profile lookup uses sub or NameIdentifier from the caller and returns exact contact details plus a refreshed JWT. |
| UT-1-04-015 | <code>claimType: "sub"</code> | Checks profile lookup uses sub or NameIdentifier from the caller and returns exact contact details plus a refreshed JWT. |

#### Profile name validation checks required and length boundaries

[ProfileValidationTests.cs:14](AuthService.UnitTests/Profile/ProfileValidationTests.cs#L14)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-016 | <code>scenario: "Empty", valid: False</code> | Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters. |
| UT-1-04-017 | <code>scenario: "MaximumLength", valid: True</code> | Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters. |
| UT-1-04-018 | <code>scenario: "MinimumLength", valid: True</code> | Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters. |
| UT-1-04-019 | <code>scenario: "TooLong", valid: False</code> | Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters. |
| UT-1-04-020 | <code>scenario: "Whitespace", valid: False</code> | Real DTO validation checks empty/whitespace names, accepted lengths 1 and 150, and rejection at 151 characters. |

#### Profile phone validation checks e164 format and length boundaries

[ProfileValidationTests.cs:36](AuthService.UnitTests/Profile/ProfileValidationTests.cs#L36)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-04-021 | <code>phone: "", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-022 | <code>phone: "+0771234567", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-023 | <code>phone: "+1234567", valid: True</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-024 | <code>phone: "+123456789012345", valid: True</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-025 | <code>phone: "+1234567890123456", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-026 | <code>phone: "+94", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-027 | <code>phone: "+94771234567", valid: True</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-028 | <code>phone: "+94abc4567", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |
| UT-1-04-029 | <code>phone: "0771234567", valid: False</code> | Real DTO validation checks required input, leading plus, initial digit, numeric format and minimum/maximum E.164 lengths. |

</details>

<a id="authservice-passwordreset"></a>

### Password Reset (34 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story PasswordReset -NoBuild`

<details><summary>All 34 individual cases in Password Reset (15 checks)</summary>

#### Invalid reset request rejects without creating or sending code

[ForgotPasswordTests.cs:57](AuthService.UnitTests/PasswordReset/ForgotPasswordTests.cs#L57)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-001 | <code>scenario: "SuspendedAccount", status: 403, error: "Your account has been suspended. Please contact su"···</code> | Unknown email or suspended account returns its current error without reset token creation, email or session issuance. |
| UT-1-05-002 | <code>scenario: "UnknownEmail", status: 400, error: "there is no account associated with this email."</code> | Unknown email or suspended account returns its current error without reset token creation, email or session issuance. |

#### Registered email invalidates old reset codes and emails hashed five minute otp

[ForgotPasswordTests.cs:14](AuthService.UnitTests/PasswordReset/ForgotPasswordTests.cs#L14)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-003 | <code>No parameters; the test body defines the fixture.</code> | Checks old-code invalidation before generation/storage/email, hashed reset token data with five-minute expiry, email content, reset session and cancellation forwarding. |

#### Consumed reset code cannot change password again when repository returns no active code

[PasswordResetControllerTests.cs:198](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L198)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-004 | <code>No parameters; the test body defines the fixture.</code> | Stubs the repository post-consumption contract; retry cannot repeat hashing, password persistence, OTP consumption or authentication. |

#### Correct reset code hashes valid password consumes code and issues cookie

[PasswordResetControllerTests.cs:51](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L51)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-005 | <code>scenario: "MaximumLength", production: False</code> | Uses the real password validator for accepted boundaries; verifies hashing before persistence/OTP consumption, success response and environment-specific auth cookie. |
| UT-1-05-006 | <code>scenario: "MinimumLength", production: False</code> | Uses the real password validator for accepted boundaries; verifies hashing before persistence/OTP consumption, success response and environment-specific auth cookie. |
| UT-1-05-007 | <code>scenario: "Normal", production: True</code> | Uses the real password validator for accepted boundaries; verifies hashing before persistence/OTP consumption, success response and environment-specific auth cookie. |

#### Invalid reset session rejects before repository access

[PasswordResetControllerTests.cs:131](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L131)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-008 | <code>No parameters; the test body defines the fixture.</code> | Invalid reset session is rejected before account/code lookups or reset side effects. |

#### No active reset code returns expiry error without changing password

[PasswordResetControllerTests.cs:142](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L142)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-009 | <code>No parameters; the test body defines the fixture.</code> | Stubs the repository inactive/expired-code response and checks rejection with no password update; does not test SQL expiry filtering. |

#### Subsequent login verifies new password against updated hash

[PasswordResetControllerTests.cs:217](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L217)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-010 | <code>No parameters; the test body defines the fixture.</code> | A repository stub stores the hash passed by reset; subsequent login must verify the new password against that hash. Password hashing/verification remains mocked. |

#### Suspended account cannot reset password or access reset codes

[PasswordResetControllerTests.cs:158](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L158)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-011 | <code>No parameters; the test body defines the fixture.</code> | Suspended accounts return HTTP 403 before reset code processing or password changes. |

#### Unverified account password is reset but authentication requires verification

[PasswordResetControllerTests.cs:173](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L173)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-012 | <code>No parameters; the test body defines the fixture.</code> | Current behaviour resets the password and consumes the OTP, then returns HTTP 403 with a verification session and no login JWT/cookie. |

#### Weak reset password returns requirements without hashing saving or consuming code

[PasswordResetControllerTests.cs:111](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L111)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-013 | <code>scenario: "NoDigit", error: "Password must contain at least one digit."</code> | Each complexity failure returns the exact password requirement after OTP validation, without hashing, password writes or code consumption. |
| UT-1-05-014 | <code>scenario: "NoLowercase", error: "Password must contain at least one lowercase lette"···</code> | Each complexity failure returns the exact password requirement after OTP validation, without hashing, password writes or code consumption. |
| UT-1-05-015 | <code>scenario: "NoUppercase", error: "Password must contain at least one uppercase lette"···</code> | Each complexity failure returns the exact password requirement after OTP validation, without hashing, password writes or code consumption. |
| UT-1-05-016 | <code>scenario: "TooLong", error: "Password must be at most 128 characters long."</code> | Each complexity failure returns the exact password requirement after OTP validation, without hashing, password writes or code consumption. |
| UT-1-05-017 | <code>scenario: "TooShort", error: "Password must be at least 8 characters long."</code> | Each complexity failure returns the exact password requirement after OTP validation, without hashing, password writes or code consumption. |

#### Wrong reset code counts failures and consumes code at ten attempt limit

[PasswordResetControllerTests.cs:86](AuthService.UnitTests/PasswordReset/PasswordResetControllerTests.cs#L86)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-018 | <code>previousAttempts: 0, locked: False</code> | Checks first, sixth, ninth and tenth wrong attempts under the current ten-attempt limit, with no password change or authentication. |
| UT-1-05-019 | <code>previousAttempts: 5, locked: False</code> | Checks first, sixth, ninth and tenth wrong attempts under the current ten-attempt limit, with no password change or authentication. |
| UT-1-05-020 | <code>previousAttempts: 8, locked: False</code> | Checks first, sixth, ninth and tenth wrong attempts under the current ten-attempt limit, with no password change or authentication. |
| UT-1-05-021 | <code>previousAttempts: 9, locked: True</code> | Checks first, sixth, ninth and tenth wrong attempts under the current ten-attempt limit, with no password change or authentication. |

#### Empty reset request reports all required fields

[PasswordResetValidationTests.cs:48](AuthService.UnitTests/PasswordReset/PasswordResetValidationTests.cs#L48)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-022 | <code>No parameters; the test body defines the fixture.</code> | A completely empty reset DTO reports Code, NewPassword and SessionToken as required. |

#### Forgot password email validation checks required format and maximum length

[PasswordResetValidationTests.cs:13](AuthService.UnitTests/PasswordReset/PasswordResetValidationTests.cs#L13)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-023 | <code>scenario: "Empty", valid: False</code> | Checks real forgot-password DTO rules for required email, malformed email, overlong email and valid input. |
| UT-1-05-024 | <code>scenario: "Malformed", valid: False</code> | Checks real forgot-password DTO rules for required email, malformed email, overlong email and valid input. |
| UT-1-05-025 | <code>scenario: "TooLong", valid: False</code> | Checks real forgot-password DTO rules for required email, malformed email, overlong email and valid input. |
| UT-1-05-026 | <code>scenario: "Valid", valid: True</code> | Checks real forgot-password DTO rules for required email, malformed email, overlong email and valid input. |

#### Reset password code validation requires exactly six digits

[PasswordResetValidationTests.cs:30](AuthService.UnitTests/PasswordReset/PasswordResetValidationTests.cs#L30)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-027 | <code>code: "", valid: False</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |
| UT-1-05-028 | <code>code: "000001", valid: True</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |
| UT-1-05-029 | <code>code: "12345", valid: False</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |
| UT-1-05-030 | <code>code: "123456", valid: True</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |
| UT-1-05-031 | <code>code: "1234567", valid: False</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |
| UT-1-05-032 | <code>code: "12a456", valid: False</code> | Checks real reset DTO validation for empty, malformed and valid six-digit codes, including leading zeroes. |

#### Reset password request requires session and new password

[PasswordResetValidationTests.cs:38](AuthService.UnitTests/PasswordReset/PasswordResetValidationTests.cs#L38)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-05-033 | <code>field: "NewPassword"</code> | Checks real reset DTO validation identifies a missing session token or new password. |
| UT-1-05-034 | <code>field: "SessionToken"</code> | Checks real reset DTO validation identifies a missing session token or new password. |

</details>

<a id="authservice-accountdeletion"></a>

### Account Deletion (14 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story AccountDeletion -NoBuild`

<details><summary>All 14 individual cases in Account Deletion (6 checks)</summary>

#### Confirmed self deletion removes tokens soft deletes publishes event and expires cookie

[AccountDeletionControllerTests.cs:20](AuthService.UnitTests/AccountDeletion/AccountDeletionControllerTests.cs#L20)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-001 | <code>claimType: "http://schemas.xmlsoap.org/ws/2005/05/identity/cla"···</code> | Checks caller identity, password verification, reset/verification token cleanup before soft deletion/event publication, exact event payload and expired auth cookie. |
| UT-1-06-002 | <code>claimType: "sub"</code> | Checks caller identity, password verification, reset/verification token cleanup before soft deletion/event publication, exact event payload and expired auth cookie. |

#### Incorrect confirmation password rejects without deleting or clearing session

[AccountDeletionControllerTests.cs:89](AuthService.UnitTests/AccountDeletion/AccountDeletionControllerTests.cs#L89)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-003 | <code>No parameters; the test body defines the fixture.</code> | Incorrect password confirmation returns an error without deleting tokens/account, publishing events or expiring the session. |

#### Ineligible self deletion rejects before password check or side effects

[AccountDeletionControllerTests.cs:72](AuthService.UnitTests/AccountDeletion/AccountDeletionControllerTests.cs#L72)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-004 | <code>scenario: "AdminAccount", status: 403, error: "Admin accounts cannot be deleted through this inte"···</code> | Missing, suspended, unverified or admin account receives its current error without password checks, writes, events or cookie clearing. |
| UT-1-06-005 | <code>scenario: "MissingAccount", status: 401, error: "User not found."</code> | Missing, suspended, unverified or admin account receives its current error without password checks, writes, events or cookie clearing. |
| UT-1-06-006 | <code>scenario: "SuspendedAccount", status: 403, error: "Your account has been suspended."</code> | Missing, suspended, unverified or admin account receives its current error without password checks, writes, events or cookie clearing. |
| UT-1-06-007 | <code>scenario: "UnverifiedAccount", status: 401, error: "Email not verified."</code> | Missing, suspended, unverified or admin account receives its current error without password checks, writes, events or cookie clearing. |

#### Missing or invalid deletion identity rejects before repository access

[AccountDeletionControllerTests.cs:59](AuthService.UnitTests/AccountDeletion/AccountDeletionControllerTests.cs#L59)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-008 | <code>subject: "invalid-id"</code> | Missing or invalid caller identity rejects account deletion before any repository or password-hasher access. |
| UT-1-06-009 | <code>subject: null</code> | Missing or invalid caller identity rejects account deletion before any repository or password-hasher access. |

#### Post deletion login rejects repository filtered account without new jwt

[AccountDeletionControllerTests.cs:104](AuthService.UnitTests/AccountDeletion/AccountDeletionControllerTests.cs#L104)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-010 | <code>No parameters; the test body defines the fixture.</code> | A repository stub hides the account after soft deletion; login returns the generic credential error without password verification or a new JWT. |

#### Deletion confirmation requires nonempty password

[AccountDeletionValidationTests.cs:13](AuthService.UnitTests/AccountDeletion/AccountDeletionValidationTests.cs#L13)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-06-011 | <code>password: "   ", valid: False</code> | Checks real deletion DTO validation rejects null, empty and whitespace confirmation passwords and accepts nonempty input. |
| UT-1-06-012 | <code>password: "", valid: False</code> | Checks real deletion DTO validation rejects null, empty and whitespace confirmation passwords and accepts nonempty input. |
| UT-1-06-013 | <code>password: "CurrentPassword123", valid: True</code> | Checks real deletion DTO validation rejects null, empty and whitespace confirmation passwords and accepts nonempty input. |
| UT-1-06-014 | <code>password: null, valid: False</code> | Checks real deletion DTO validation rejects null, empty and whitespace confirmation passwords and accepts nonempty input. |

</details>

<a id="authservice-adminusermanagement"></a>

### Admin User Management (22 cases)

**Run:** `.\test-unit.ps1 -Service AuthService -Story AdminUserManagement -NoBuild`

<details><summary>All 22 individual cases in Admin User Management (7 checks)</summary>

#### Confirmed admin kick sets suspension publishes actor event and blocks subsequent login

[AdminKickTests.cs:14](AuthService.UnitTests/AdminUserManagement/AdminKickTests.cs#L14)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-001 | <code>No parameters; the test body defines the fixture.</code> | Checks suspension write before an exact event identifying the admin; a subsequent login against the stubbed suspended account is rejected without JWT issuance. |

#### Invalid admin kick rejects without suspension or publication

[AdminKickTests.cs:52](AuthService.UnitTests/AdminUserManagement/AdminKickTests.cs#L52)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-002 | <code>scenario: "AdminTarget", status: 400, error: "Cannot kick an admin account."</code> | Missing confirmation, self-kick, missing user, admin target or already-kicked user reject without suspension writes or event publication. |
| UT-1-07-003 | <code>scenario: "AlreadyKicked", status: 400, error: "User is already kicked."</code> | Missing confirmation, self-kick, missing user, admin target or already-kicked user reject without suspension writes or event publication. |
| UT-1-07-004 | <code>scenario: "MissingUser", status: 404, error: "User not found."</code> | Missing confirmation, self-kick, missing user, admin target or already-kicked user reject without suspension writes or event publication. |
| UT-1-07-005 | <code>scenario: "NoConfirmation", status: 400, error: "Confirmation is required. Set confirm to true."</code> | Missing confirmation, self-kick, missing user, admin target or already-kicked user reject without suspension writes or event publication. |
| UT-1-07-006 | <code>scenario: "Self", status: 400, error: "You cannot kick yourself."</code> | Missing confirmation, self-kick, missing user, admin target or already-kicked user reject without suspension writes or event publication. |

#### User management policy checks actual admin account and guards list and kick

[AdminManagementAuthorizationTests.cs:20](AuthService.UnitTests/AdminUserManagement/AdminManagementAuthorizationTests.cs#L20)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-007 | <code>scenario: "ActiveAdmin", allowed: True</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-008 | <code>scenario: "DeletedAdmin", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-009 | <code>scenario: "ForgedAdminClaim", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-010 | <code>scenario: "InvalidIdentity", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-011 | <code>scenario: "MissingAccount", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-012 | <code>scenario: "RegularUser", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |
| UT-1-07-013 | <code>scenario: "SuspendedAdmin", allowed: False</code> | Runs the real AdminOnly handler, including a forged admin claim against a regular account, and checks management endpoints inherit the policy without AllowAnonymous. |

#### Admin pagination clamps bounds and computes repository offset

[AdminUserListTests.cs:87](AuthService.UnitTests/AdminUserManagement/AdminUserListTests.cs#L87)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-014 | <code>page: 0, size: 0, expectedPage: 1, expectedSize: 1, expectedOffset: 0</code> | Parameterized page/page-size cases verify lower/upper clamps, exact repository offset and total-page calculation. |
| UT-1-07-015 | <code>page: 2, size: 101, expectedPage: 2, expectedSize: 100, expectedOffset: 100</code> | Parameterized page/page-size cases verify lower/upper clamps, exact repository offset and total-page calculation. |
| UT-1-07-016 | <code>page: 3, size: 10, expectedPage: 3, expectedSize: 10, expectedOffset: 20</code> | Parameterized page/page-size cases verify lower/upper clamps, exact repository offset and total-page calculation. |

#### Admin search and status filters are forwarded to both list and count

[AdminUserListTests.cs:46](AuthService.UnitTests/AdminUserManagement/AdminUserListTests.cs#L46)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-017 | <code>search: "Spam", kicked: null, verified: null, deleted: null</code> | Name/email search and kicked/verified/deleted filters reach both repository operations unchanged; checks the returned DTO and counts, not SQL matching. |
| UT-1-07-018 | <code>search: "spam@example.test", kicked: null, verified: null, deleted: null</code> | Name/email search and kicked/verified/deleted filters reach both repository operations unchanged; checks the returned DTO and counts, not SQL matching. |
| UT-1-07-019 | <code>search: null, kicked: null, verified: null, deleted: True</code> | Name/email search and kicked/verified/deleted filters reach both repository operations unchanged; checks the returned DTO and counts, not SQL matching. |
| UT-1-07-020 | <code>search: null, kicked: True, verified: False, deleted: False</code> | Name/email search and kicked/verified/deleted filters reach both repository operations unchanged; checks the returned DTO and counts, not SQL matching. |

#### Admin user list returns profile status and pagination without password hashes

[AdminUserListTests.cs:14](AuthService.UnitTests/AdminUserManagement/AdminUserListTests.cs#L14)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-021 | <code>No parameters; the test body defines the fixture.</code> | Checks exact account/contact/status projection, omission of password hashes, pagination counts and cancellation propagation. |

#### Empty admin search returns empty list and zero pages

[AdminUserListTests.cs:69](AuthService.UnitTests/AdminUserManagement/AdminUserListTests.cs#L69)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-1-07-022 | <code>No parameters; the test body defines the fixture.</code> | An empty repository search result yields an empty user list, total zero and zero pages. |

</details>

<a id="itemservice-catalogue"></a>

## Item Service (115 cases)

| Story | Cases | Tested scope |
|---|---:|---|
| [Report Lost Item](#itemservice-reportlostitem) | 21 | Lost-report creation, field/photo validation, event contents and public response privacy. |
| [Report Found Item](#itemservice-reportfounditem) | 21 | Found-report creation, field/photo validation, event contents and public response privacy. |
| [Edit Item Report](#itemservice-edititemreport) | 14 | Owner checks, update validation, saved state and complete update-event contents. |
| [Search And Filter Items](#itemservice-searchandfilteritems) | 17 | Search/filter arguments passed to the repository, pagination, invalid input and public projection privacy. |
| [Mark Item Resolved](#itemservice-markitemresolved) | 12 | Owner-only resolution, state changes, resolve events and the report-history response. |
| [View Item Details](#itemservice-viewitemdetails) | 10 | Public item fields, hidden-information exclusion, caller identity and resolved/missing-item responses. |
| [Delete Item Report](#itemservice-deleteitemreport) | 13 | Owner-only soft deletion, confirmed-match restrictions, publication order and deleted-item responses. |
| [My Reports](#itemservice-myreports) | 7 | JWT-scoped report history, current statuses, empty results and private-field exclusion. |

<a id="itemservice-reportlostitem"></a>

### Report Lost Item (21 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story ReportLostItem -NoBuild`

<details><summary>All 21 individual cases in Report Lost Item (9 checks)</summary>

#### Future Date Is Rejected Without Persistence

[ReportLostItemTests.cs:139](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L139)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-001 | <code>No parameters; the test body defines the fixture.</code> | Checks a date after the permitted current date is rejected before saving the report. |

#### Invalid Category Or Date Returns Validation Error

[ReportLostItemTests.cs:125](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L125)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-002 | <code>field: "Category", value: "Invalid category"</code> | Checks the supplied unsupported category or malformed date is rejected with the expected field error. |
| UT-2-01-003 | <code>field: "DateLost", value: "14/10/2026"</code> | Checks the supplied unsupported category or malformed date is rejected with the expected field error. |

#### Invalid Photos Are Rejected Before Persistence

[ReportLostItemTests.cs:167](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L167)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-004 | <code>kind: "spoofed"</code> | Checks the supplied invalid photo input is rejected before writing an item. |
| UT-2-01-005 | <code>kind: "too-many"</code> | Checks the supplied invalid photo input is rejected before writing an item. |
| UT-2-01-006 | <code>kind: "unsupported"</code> | Checks the supplied invalid photo input is rejected before writing an item. |

#### Length Limits Are Declared On The Request Dto

[ReportLostItemTests.cs:155](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L155)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-007 | <code>field: "Category", length: 51</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-01-008 | <code>field: "Description", length: 2001</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-01-009 | <code>field: "HiddenInformation", length: 501</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-01-010 | <code>field: "LastKnownLocation", length: 256</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-01-011 | <code>field: "Title", length: 151</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |

#### Missing Required Field Returns Validation Error Without Writing

[ReportLostItemTests.cs:109](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L109)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-012 | <code>field: "Category"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-01-013 | <code>field: "DateLost"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-01-014 | <code>field: "Description"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-01-015 | <code>field: "HiddenInformation"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-01-016 | <code>field: "LastKnownLocation"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-01-017 | <code>field: "Title"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |

#### Missing User Claim Cannot Create Report

[ReportLostItemTests.cs:184](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L184)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-018 | <code>No parameters; the test body defines the fixture.</code> | Checks report creation rejects a caller with no usable user identity before persistence. |

#### Optional Photo Is Stored Linked And Included In Event And Response

[ReportLostItemTests.cs:66](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L66)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-019 | <code>No parameters; the test body defines the fixture.</code> | Checks that a supplied photo is associated with the saved item and its URL is included in the creation event and public response. |

#### Public Read Does Not Expose Stored Hidden Information

[ReportLostItemTests.cs:193](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L193)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-020 | <code>No parameters; the test body defines the fixture.</code> | Checks a public item projection omits the hidden information held by the internal item record. |

#### Valid Report Persists Active Item And Publishes Full Private Event

[ReportLostItemTests.cs:15](ItemService.UnitTests/ReportLostItem/ReportLostItemTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-01-021 | <code>No parameters; the test body defines the fixture.</code> | Checks the active record passed to the repository and the complete creation-event payload, including hidden information, reporter identity and timestamps. |

</details>

<a id="itemservice-reportfounditem"></a>

### Report Found Item (21 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story ReportFoundItem -NoBuild`

<details><summary>All 21 individual cases in Report Found Item (9 checks)</summary>

#### Future Date Is Rejected Without Persistence

[ReportFoundItemTests.cs:135](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L135)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-001 | <code>No parameters; the test body defines the fixture.</code> | Checks a date after the permitted current date is rejected before saving the report. |

#### Invalid Category Or Date Returns Validation Error

[ReportFoundItemTests.cs:124](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L124)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-002 | <code>field: "Category", value: "Unsupported"</code> | Checks the supplied unsupported category or malformed date is rejected with the expected field error. |
| UT-2-02-003 | <code>field: "DateFound", value: "14/10/2026"</code> | Checks the supplied unsupported category or malformed date is rejected with the expected field error. |

#### Invalid Photos Are Rejected Before Persistence

[ReportFoundItemTests.cs:162](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L162)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-004 | <code>kind: "spoofed"</code> | Checks the supplied invalid photo input is rejected before writing an item. |
| UT-2-02-005 | <code>kind: "too-many"</code> | Checks the supplied invalid photo input is rejected before writing an item. |
| UT-2-02-006 | <code>kind: "unsupported"</code> | Checks the supplied invalid photo input is rejected before writing an item. |

#### Length Limits Are Declared On The Request Dto

[ReportFoundItemTests.cs:151](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L151)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-007 | <code>field: "Category", length: 51</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-02-008 | <code>field: "Description", length: 2001</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-02-009 | <code>field: "HiddenInformation", length: 501</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-02-010 | <code>field: "LocationFound", length: 256</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |
| UT-2-02-011 | <code>field: "Title", length: 151</code> | Inspects the real request DTO for the length constraint on the specified field; this checks declared metadata rather than HTTP middleware. |

#### Missing Required Field Returns Validation Error Without Writing

[ReportFoundItemTests.cs:108](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L108)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-012 | <code>field: "Category"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-02-013 | <code>field: "DateFound"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-02-014 | <code>field: "Description"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-02-015 | <code>field: "HiddenInformation"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-02-016 | <code>field: "LocationFound"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |
| UT-2-02-017 | <code>field: "Title"</code> | Checks the named required input produces a field error and neither persistence nor event publication is requested. |

#### Missing User Claim Cannot Create Report

[ReportFoundItemTests.cs:179](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L179)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-018 | <code>No parameters; the test body defines the fixture.</code> | Checks report creation rejects a caller with no usable user identity before persistence. |

#### Optional Photo Is Stored Linked And Included In Event And Response

[ReportFoundItemTests.cs:65](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L65)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-019 | <code>No parameters; the test body defines the fixture.</code> | Checks that a supplied photo is associated with the saved item and its URL is included in the creation event and public response. |

#### Public Read Does Not Expose Stored Hidden Information

[ReportFoundItemTests.cs:188](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L188)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-020 | <code>No parameters; the test body defines the fixture.</code> | Checks a public item projection omits the hidden information held by the internal item record. |

#### Valid Report Persists Active Item And Publishes Full Private Event

[ReportFoundItemTests.cs:15](ItemService.UnitTests/ReportFoundItem/ReportFoundItemTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-02-021 | <code>No parameters; the test body defines the fixture.</code> | Checks the active record passed to the repository and the complete creation-event payload, including hidden information, reporter identity and timestamps. |

</details>

<a id="itemservice-edititemreport"></a>

### Edit Item Report (14 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story EditItemReport -NoBuild`

<details><summary>All 14 individual cases in Edit Item Report (7 checks)</summary>

#### Invalid Update Returns Field Errors Without Saving Or Publishing

[EditItemReportTests.cs:141](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L141)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-001 | <code>kind: "found"</code> | Checks invalid update input returns the named field error before any update or event publication. |
| UT-2-03-002 | <code>kind: "lost"</code> | Checks invalid update input returns the named field error before any update or event publication. |

#### Missing Identity Returns401 Without Repository Access

[EditItemReportTests.cs:209](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L209)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-003 | <code>kind: "found"</code> | Checks an update without a caller identity returns 401 before consulting repositories. |
| UT-2-03-004 | <code>kind: "lost"</code> | Checks an update without a caller identity returns 401 before consulting repositories. |

#### Non Owner Edit Returns403 Without Saving Or Publishing

[EditItemReportTests.cs:112](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L112)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-005 | <code>kind: "found"</code> | Checks a caller who does not own the item receives 403 and no save or publication is requested. |
| UT-2-03-006 | <code>kind: "lost"</code> | Checks a caller who does not own the item receives 403 and no save or publication is requested. |

#### Overlong Update Fields Are Rejected By Dto Validation

[EditItemReportTests.cs:231](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L231)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-007 | <code>kind: "found", field: "Description", length: 2001</code> | Runs the real DTO validator for the specified overlong update field and item type. |
| UT-2-03-008 | <code>kind: "found", field: "LocationFound", length: 256</code> | Runs the real DTO validator for the specified overlong update field and item type. |
| UT-2-03-009 | <code>kind: "lost", field: "HiddenInformation", length: 501</code> | Runs the real DTO validator for the specified overlong update field and item type. |
| UT-2-03-010 | <code>kind: "lost", field: "Title", length: 151</code> | Runs the real DTO validator for the specified overlong update field and item type. |

#### Owner Updates Found Report Then Publishes Complete Current State

[EditItemReportTests.cs:63](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L63)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-011 | <code>No parameters; the test body defines the fixture.</code> | Checks the found-report owner can save changes and publication follows the write with the full current item, including hidden information. |

#### Owner Updates Lost Report Then Publishes Complete Current State

[EditItemReportTests.cs:15](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-012 | <code>No parameters; the test body defines the fixture.</code> | Checks the lost-report owner can save changes and publication follows the write with the full current item, including hidden information. |

#### Unknown Report Returns404

[EditItemReportTests.cs:185](ItemService.UnitTests/EditItemReport/EditItemReportTests.cs#L185)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-03-013 | <code>kind: "found"</code> | Checks the controller returns 404 when the mocked repository reports no item. |
| UT-2-03-014 | <code>kind: "lost"</code> | Checks the controller returns 404 when the mocked repository reports no item. |

</details>

<a id="itemservice-searchandfilteritems"></a>

### Search And Filter Items (17 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story SearchAndFilterItems -NoBuild`

<details><summary>All 17 individual cases in Search And Filter Items (10 checks)</summary>

#### Browse Without Filters Requests Default First Page And Returns Repository Page

[SearchAndFilterItemsTests.cs:12](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L12)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the default page (1), size (20), absent filters and returned repository page; no SQL search is executed. |

#### Category Filter Is Passed To Repository

[SearchAndFilterItemsTests.cs:59](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L59)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-002 | <code>No parameters; the test body defines the fixture.</code> | Checks the selected category reaches the search query unchanged. |

#### Combined Filters Reach Repository Together With Pagination

[SearchAndFilterItemsTests.cs:90](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L90)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-003 | <code>No parameters; the test body defines the fixture.</code> | Checks keyword, category, type, date range, page and page size are forwarded together in one search query. |

#### Date Range Filter Uses Inclusive Bounds And Supports Equal Dates

[SearchAndFilterItemsTests.cs:74](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L74)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-004 | <code>No parameters; the test body defines the fixture.</code> | Checks equal from/to dates are accepted and forwarded as the expected date bounds; database inclusion is outside this test. |

#### Invalid Filters Are Rejected Before Repository Call

[SearchAndFilterItemsTests.cs:135](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L135)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-005 | <code>field: "category", value: "Unsupported", errorKey: "Category"</code> | Checks unsupported category/type or malformed date filters produce the expected validation key with no search call. |
| UT-2-04-006 | <code>field: "dateFrom", value: "09/01/2026", errorKey: "DateFrom"</code> | Checks unsupported category/type or malformed date filters produce the expected validation key with no search call. |
| UT-2-04-007 | <code>field: "dateTo", value: "30-09-2026", errorKey: "DateTo"</code> | Checks unsupported category/type or malformed date filters produce the expected validation key with no search call. |
| UT-2-04-008 | <code>field: "type", value: "RESOLVED", errorKey: "Type"</code> | Checks unsupported category/type or malformed date filters produce the expected validation key with no search call. |

#### Invalid Pagination Is Rejected Before Repository Call

[SearchAndFilterItemsTests.cs:153](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L153)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-009 | <code>page: 0, pageSize: 20, errorKey: "Page"</code> | Checks the supplied invalid page or page size is rejected before querying items. |
| UT-2-04-010 | <code>page: 1, pageSize: 0, errorKey: "PageSize"</code> | Checks the supplied invalid page or page size is rejected before querying items. |
| UT-2-04-011 | <code>page: 1, pageSize: 51, errorKey: "PageSize"</code> | Checks the supplied invalid page or page size is rejected before querying items. |

#### Keyword Search Trims Input Before Querying Repository

[SearchAndFilterItemsTests.cs:44](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L44)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-012 | <code>input: "  library entrance  ", expected: "library entrance"</code> | Checks leading/trailing keyword whitespace is removed and the expected text reaches the search repository. |
| UT-2-04-013 | <code>input: "  WALLET  ", expected: "WALLET"</code> | Checks leading/trailing keyword whitespace is removed and the expected text reaches the search repository. |
| UT-2-04-014 | <code>input: "wallet", expected: "wallet"</code> | Checks leading/trailing keyword whitespace is removed and the expected text reaches the search repository. |

#### No Matching Results Returns Empty Page Rather Than Error

[SearchAndFilterItemsTests.cs:114](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L114)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-015 | <code>No parameters; the test body defines the fixture.</code> | Checks an empty repository result becomes a successful empty page with zero totals. |

#### Public Search Summary Has No Hidden Information Field

[SearchAndFilterItemsTests.cs:171](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L171)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-016 | <code>No parameters; the test body defines the fixture.</code> | Inspects the public summary DTO properties to ensure no hidden-information property is declared. |

#### Reversed Date Range Is Rejected Before Repository Call

[SearchAndFilterItemsTests.cs:162](ItemService.UnitTests/SearchAndFilterItems/SearchAndFilterItemsTests.cs#L162)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-04-017 | <code>No parameters; the test body defines the fixture.</code> | Checks a from-date after the to-date produces a DateRange error without repository access. |

</details>

<a id="itemservice-markitemresolved"></a>

### Mark Item Resolved (12 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story MarkItemResolved -NoBuild`

<details><summary>All 12 individual cases in Mark Item Resolved (7 checks)</summary>

#### Already Resolved Item Returns Conflict Without Another Write

[MarkItemResolvedTests.cs:165](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L165)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-001 | <code>kind: "found"</code> | Checks resolving an already-resolved item returns a conflict without another write or event. |
| UT-2-05-002 | <code>kind: "lost"</code> | Checks resolving an already-resolved item returns a conflict without another write or event. |

#### Missing Identity Returns401 Before Lookup

[MarkItemResolvedTests.cs:193](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L193)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-003 | <code>kind: "found"</code> | Checks resolution without caller identity returns 401 before item lookup. |
| UT-2-05-004 | <code>kind: "lost"</code> | Checks resolution without caller identity returns 401 before item lookup. |

#### Missing Item Returns404 Without Publishing

[MarkItemResolvedTests.cs:141](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L141)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-005 | <code>kind: "found"</code> | Checks a missing item returns 404 with no resolution event. |
| UT-2-05-006 | <code>kind: "lost"</code> | Checks a missing item returns 404 with no resolution event. |

#### Non Owner Cannot Resolve Or Publish

[MarkItemResolvedTests.cs:112](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L112)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-007 | <code>kind: "found"</code> | Checks a non-owner cannot change resolution status or publish a resolve event. |
| UT-2-05-008 | <code>kind: "lost"</code> | Checks a non-owner cannot change resolution status or publish a resolve event. |

#### Owner Resolves Found Item Status Is Saved Before Full Event Is Published

[MarkItemResolvedTests.cs:64](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L64)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-009 | <code>No parameters; the test body defines the fixture.</code> | Checks the found-item owner saves RESOLVED before requesting a resolve event containing the complete current item. |

#### Owner Resolves Lost Item Status Is Saved Before Full Event Is Published

[MarkItemResolvedTests.cs:15](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-010 | <code>No parameters; the test body defines the fixture.</code> | Checks the lost-item owner saves RESOLVED before requesting a resolve event containing the complete current item. |

#### Resolved Item Remains In Owners Report History

[MarkItemResolvedTests.cs:207](ItemService.UnitTests/MarkItemResolved/MarkItemResolvedTests.cs#L207)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-05-011 | <code>kind: "found"</code> | Checks the owner-history response can include the mocked resolved item. |
| UT-2-05-012 | <code>kind: "lost"</code> | Checks the owner-history response can include the mocked resolved item. |

</details>

<a id="itemservice-viewitemdetails"></a>

### View Item Details (10 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story ViewItemDetails -NoBuild`

<details><summary>All 10 individual cases in View Item Details (7 checks)</summary>

#### Found Item Details Return All Public Fields Without Hidden Information

[ViewItemDetailsTests.cs:41](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L41)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-001 | <code>No parameters; the test body defines the fixture.</code> | Checks found-item detail fields and photo projection while excluding hidden information. |

#### Lost Item Details Return All Public Fields Without Hidden Information

[ViewItemDetailsTests.cs:13](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L13)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-002 | <code>No parameters; the test body defines the fixture.</code> | Checks lost-item detail fields and photo projection while excluding hidden information. |

#### Missing User Identity Returns401 Before Repository Lookup

[ViewItemDetailsTests.cs:145](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L145)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-003 | <code>No parameters; the test body defines the fixture.</code> | Checks detail retrieval without a user identity returns 401 before item lookup. |

#### Public Detail Contract Has Neither Private Information Nor User Id

[ViewItemDetailsTests.cs:155](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L155)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-004 | <code>No parameters; the test body defines the fixture.</code> | Checks public detail DTO properties omit hidden information and internal user ID. |

#### Resolved Item Is404 For Non Owner

[ViewItemDetailsTests.cs:87](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L87)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-005 | <code>kind: "found"</code> | Checks a resolved item is reported as not found to a caller who does not own it. |
| UT-2-06-006 | <code>kind: "lost"</code> | Checks a resolved item is reported as not found to a caller who does not own it. |

#### Resolved Item Remains Visible To Original Reporter

[ViewItemDetailsTests.cs:118](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L118)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-007 | <code>kind: "found"</code> | Checks the original reporter can still read their resolved item. |
| UT-2-06-008 | <code>kind: "lost"</code> | Checks the original reporter can still read their resolved item. |

#### Unknown Or Repository Filtered Deleted Item Returns404

[ViewItemDetailsTests.cs:70](ItemService.UnitTests/ViewItemDetails/ViewItemDetailsTests.cs#L70)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-06-009 | <code>emptyId: False</code> | Checks a missing/filtered repository result returns 404; the repository performs the deletion filtering outside this test. |
| UT-2-06-010 | <code>emptyId: True</code> | Checks a missing/filtered repository result returns 404; the repository performs the deletion filtering outside this test. |

</details>

<a id="itemservice-deleteitemreport"></a>

### Delete Item Report (13 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story DeleteItemReport -NoBuild`

<details><summary>All 13 individual cases in Delete Item Report (7 checks)</summary>

#### Confirmed Match Requires Resolution Instead Of Deletion

[DeleteItemReportTests.cs:107](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L107)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-001 | <code>type: "FOUND"</code> | Checks a confirmed-match response blocks deletion and tells the caller to resolve the item. |
| UT-2-07-002 | <code>type: "LOST"</code> | Checks a confirmed-match response blocks deletion and tells the caller to resolve the item. |

#### Failed Soft Delete Does Not Publish Delete Event

[DeleteItemReportTests.cs:179](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L179)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-003 | <code>type: "FOUND"</code> | Checks an unsuccessful repository soft-delete cannot cause a delete event. |
| UT-2-07-004 | <code>type: "LOST"</code> | Checks an unsuccessful repository soft-delete cannot cause a delete event. |

#### Missing Identity Returns401 Before Repository Access

[DeleteItemReportTests.cs:163](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L163)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-005 | <code>type: "FOUND"</code> | Checks deletion without caller identity returns 401 before repository access. |
| UT-2-07-006 | <code>type: "LOST"</code> | Checks deletion without caller identity returns 401 before repository access. |

#### Missing Report Returns404 Without Publishing

[DeleteItemReportTests.cs:136](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L136)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-007 | <code>type: "FOUND"</code> | Checks deletion of a repository-missing item returns 404 without publication. |
| UT-2-07-008 | <code>type: "LOST"</code> | Checks deletion of a repository-missing item returns 404 without publication. |

#### Non Owner Cannot Delete Or Publish

[DeleteItemReportTests.cs:78](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L78)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-009 | <code>type: "FOUND"</code> | Checks a different user cannot soft-delete the item or publish its deletion. |
| UT-2-07-010 | <code>type: "LOST"</code> | Checks a different user cannot soft-delete the item or publish its deletion. |

#### Owner Deletes Active Report Before Publishing Its Id And Type

[DeleteItemReportTests.cs:16](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L16)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-011 | <code>type: "FOUND"</code> | Checks owner deletion calls soft-delete before requesting the delete event with item ID and item type. |
| UT-2-07-012 | <code>type: "LOST"</code> | Checks owner deletion calls soft-delete before requesting the delete event with item ID and item type. |

#### When Repository Filters Soft Deleted Report Public Details Return404

[DeleteItemReportTests.cs:209](ItemService.UnitTests/DeleteItemReport/DeleteItemReportTests.cs#L209)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-07-013 | <code>No parameters; the test body defines the fixture.</code> | Checks public details return 404 when the repository hides a soft-deleted item; SQL filtering is stubbed. |

</details>

<a id="itemservice-myreports"></a>

### My Reports (7 cases)

**Run:** `.\test-unit.ps1 -Service ItemService -Story MyReports -NoBuild`

<details><summary>All 7 individual cases in My Reports (4 checks)</summary>

#### Jwt Subject Scopes History To Caller Even If Name Identifier Differs

[MyReportsTests.cs:99](ItemService.UnitTests/MyReports/MyReportsTests.cs#L99)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-08-001 | <code>type: "FOUND"</code> | Checks history lookup uses the JWT subject when the alternate identity claim refers to someone else. |
| UT-2-08-002 | <code>type: "LOST"</code> | Checks history lookup uses the JWT subject when the alternate identity claim refers to someone else. |

#### Missing Identity Cannot Read Any Report History

[MyReportsTests.cs:84](ItemService.UnitTests/MyReports/MyReportsTests.cs#L84)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-08-003 | <code>type: "FOUND"</code> | Checks no report-history query is made for a caller without a valid identity. |
| UT-2-08-004 | <code>type: "LOST"</code> | Checks no report-history query is made for a caller without a valid identity. |

#### No Reports Returns An Empty List

[MyReportsTests.cs:59](ItemService.UnitTests/MyReports/MyReportsTests.cs#L59)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-08-005 | <code>type: "FOUND"</code> | Checks an empty history result returns an empty collection successfully. |
| UT-2-08-006 | <code>type: "LOST"</code> | Checks an empty history result returns an empty collection successfully. |

#### Own Lost And Found Reports Are Returned With Current Statuses And Without Private Details

[MyReportsTests.cs:14](ItemService.UnitTests/MyReports/MyReportsTests.cs#L14)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-2-08-007 | <code>No parameters; the test body defines the fixture.</code> | Checks owner history includes lost/found reports with their current statuses while omitting private fields. |

</details>

<a id="matchingservice-catalogue"></a>

## Matching Service (136 cases)

| Story | Cases | Tested scope |
|---|---:|---|
| [Photo Description Pipeline](#matchingservice-photodescriptionpipeline) | 19 | Photo-event validation/deduplication and analysis lifecycle using mocked AI/storage dependencies. |
| [Preview And Claim Match](#matchingservice-previewandclaimmatch) | 16 | Candidate selection, comparison scoring, preview versions and pre-write claim validation. |
| [View Potential Matches](#matchingservice-viewpotentialmatches) | 19 | Caller-scoped match queries, pagination, row projections, review visibility and current section rules. |
| [Lost Reporter Decision](#matchingservice-lostreporterdecision) | 9 | Lost-reporter review/action state, caller identity, unrelated-user rejection and contact-response privacy. |
| [Finder Decision](#matchingservice-finderdecision) | 9 | Finder review/action state, caller identity, unrelated-user rejection and contact-response privacy. |
| [Matching Notifications](#matchingservice-matchingnotifications) | 9 | Outcome email templates, recipient/link validation and exclusion of contact/hidden information. |
| [Deactivate Matches](#matchingservice-deactivatematches) | 10 | Lifecycle-event mapping/validation, active-match query rules and deactivation audit projection. |
| [Reevaluate Matches On Item Update](#matchingservice-reevaluatematchesonitemupdate) | 10 | Updated-report scoring and snapshot changes, optional image evidence and update-event validation. |
| [Send Match Appeal](#matchingservice-sendmatchappeal) | 16 | Appeal authorization, request validation, report eligibility and caller-facing appeal projections. |
| [Admin Match Appeal Review](#matchingservice-adminmatchappealreview) | 19 | Admin authorization, appeal filters, repeat-decision guards and review/audit projections. |

<a id="matchingservice-photodescriptionpipeline"></a>

### Photo Description Pipeline (19 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story PhotoDescriptionPipeline -NoBuild`

<details><summary>All 19 individual cases in Photo Description Pipeline (11 checks)</summary>

#### Event Without Id Is Rejected Before Any Description Is Stored

[PhotoDescriptionPipelineTests.cs:107](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L107)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-001 | <code>No parameters; the test body defines the fixture.</code> | Checks a photo event without a usable event ID is rejected before description storage. |

#### Event Without Photos Creates No Analysis Job

[PhotoDescriptionPipelineTests.cs:87](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L87)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-002 | <code>No parameters; the test body defines the fixture.</code> | Checks a valid item event with no photos queues no image-analysis record. |

#### Generated Description Preserves Visible Attributes For Matching

[PhotoDescriptionPipelineTests.cs:142](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L142)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-003 | <code>No parameters; the test body defines the fixture.</code> | Runs the real description validator and checks visible object type, colour, material and pattern values are retained. |

#### Generated Description Rejects Private Contact Details

[PhotoDescriptionPipelineTests.cs:132](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L132)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-004 | <code>privateText: "0771234567"</code> | Checks the real validator rejects the supplied email address, phone number or private URL in generated text. |
| UT-3-01-005 | <code>privateText: "https://private.example.com"</code> | Checks the real validator rejects the supplied email address, phone number or private URL in generated text. |
| UT-3-01-006 | <code>privateText: "someone@example.com"</code> | Checks the real validator rejects the supplied email address, phone number or private URL in generated text. |

#### Item Events Queue The Correct Photo As Pending Without Calling Ai

[PhotoDescriptionPipelineTests.cs:22](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L22)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-007 | <code>topic: "items.found_item.created", expectedType: "FOUND", expectedEventType: "CREATED"</code> | Checks each lost/found created/updated topic queues the correct item, URL, source event/time, pending status and zero attempts without calling AI. |
| UT-3-01-008 | <code>topic: "items.found_item.updated", expectedType: "FOUND", expectedEventType: "UPDATED"</code> | Checks each lost/found created/updated topic queues the correct item, URL, source event/time, pending status and zero attempts without calling AI. |
| UT-3-01-009 | <code>topic: "items.lost_item.created", expectedType: "LOST", expectedEventType: "CREATED"</code> | Checks each lost/found created/updated topic queues the correct item, URL, source event/time, pending status and zero attempts without calling AI. |
| UT-3-01-010 | <code>topic: "items.lost_item.updated", expectedType: "LOST", expectedEventType: "UPDATED"</code> | Checks each lost/found created/updated topic queues the correct item, URL, source event/time, pending status and zero attempts without calling AI. |

#### Redelivered Photo Event Reuses The Same Photo Key And Queues No Second Description

[PhotoDescriptionPipelineTests.cs:50](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L50)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-011 | <code>No parameters; the test body defines the fixture.</code> | Checks two deliveries reuse the same photo key; a repository stub accepts the key once and rejects duplicate insertion. |

#### Relative Photo Path Is Not Queued For External Ai Analysis

[PhotoDescriptionPipelineTests.cs:97](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L97)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-012 | <code>No parameters; the test body defines the fixture.</code> | Checks a relative photo path is not queued for an external image-analysis request. |

#### Repeated Url Inside One Event Is Queued Only Once

[PhotoDescriptionPipelineTests.cs:75](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L75)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-013 | <code>No parameters; the test body defines the fixture.</code> | Checks duplicate photo URLs inside one event produce only one pending insertion request. |

#### Unsupported Event Is Rejected Before Queuing

[PhotoDescriptionPipelineTests.cs:120](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionPipelineTests.cs#L120)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-014 | <code>topic: "items.lost_item.resolved"</code> | Checks the supplied unsupported item-event topic is rejected without queuing descriptions. |
| UT-3-01-015 | <code>topic: "items.other_item.created"</code> | Checks the supplied unsupported item-event topic is rejected without queuing descriptions. |

#### Analysis Failure Stores Safe Code And Retries Only Within Attempt Budget

[PhotoDescriptionWorkerTests.cs:48](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionWorkerTests.cs#L48)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-016 | <code>attempts: 1, retryExpected: True</code> | Checks a mocked analysis failure records a safe failure code and chooses retry/final-failure according to the supplied attempt budget; no AI request is sent. |
| UT-3-01-017 | <code>attempts: 3, retryExpected: False</code> | Checks a mocked analysis failure records a safe failure code and chooses retry/final-failure according to the supplied attempt budget; no AI request is sent. |

#### Completed Analysis Saves Description Against Claimed Item And Photo

[PhotoDescriptionWorkerTests.cs:18](MatchingService.UnitTests/PhotoDescriptionPipeline/PhotoDescriptionWorkerTests.cs#L18)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-01-018 | <code>type: "Found"</code> | Checks successful mocked analysis is saved as completed against the claimed item/photo identity and URL. |
| UT-3-01-019 | <code>type: "Lost"</code> | Checks successful mocked analysis is saved as completed against the claimed item/photo identity and URL. |

</details>

<a id="matchingservice-previewandclaimmatch"></a>

### Preview And Claim Match (16 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story PreviewAndClaimMatch -NoBuild`

<details><summary>All 16 individual cases in Preview And Claim Match (13 checks)</summary>

#### Candidate Lookup Returns Only The Callers Active Reports

[PreviewAndClaimMatchTests.cs:13](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L13)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-001 | <code>type: "FOUND", expectedPath: "api/items/found/mine"</code> | Checks candidate selection returns the caller-owned active reports of the relevant type from stubbed item data. |
| UT-3-02-002 | <code>type: "LOST", expectedPath: "api/items/lost/mine"</code> | Checks candidate selection returns the caller-owned active reports of the relevant type from stubbed item data. |

#### Claim Rechecks Active Status After Preview Before Any Database Write

[PreviewAndClaimMatchTests.cs:199](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L199)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-003 | <code>No parameters; the test body defines the fixture.</code> | Checks claim submission rejects a report that became inactive after preview, before creating a match. |

#### Claim Rechecks Ownership After Preview Before Any Database Write

[PreviewAndClaimMatchTests.cs:182](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L182)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-004 | <code>No parameters; the test body defines the fixture.</code> | Checks claim submission revalidates current ownership after preview before writing a match. |

#### Claim Without Preview Version Is Rejected Before Calling Other Services

[PreviewAndClaimMatchTests.cs:171](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L171)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-005 | <code>No parameters; the test body defines the fixture.</code> | Checks a missing preview version rejects a claim before consulting other services. |

#### Date And Location Are Shown But Do Not Change The Score

[PreviewAndClaimMatchTests.cs:60](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L60)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-006 | <code>No parameters; the test body defines the fixture.</code> | Checks comparison output retains dates/locations while changes to those fields leave the similarity score unchanged. |

#### Low Similarity Is Below Claim Threshold

[PreviewAndClaimMatchTests.cs:76](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L76)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-007 | <code>No parameters; the test body defines the fixture.</code> | Checks deliberately dissimilar reports score below the claim threshold. |

#### One Photo Missing Does Not Penalize The Text Comparison

[PreviewAndClaimMatchTests.cs:48](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L48)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-008 | <code>No parameters; the test body defines the fixture.</code> | Checks comparison proceeds using normalized text/category evidence when one report lacks a photo. |

#### Preview Rejects An Inactive Report

[PreviewAndClaimMatchTests.cs:138](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L138)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-009 | <code>lostStatus: "ACTIVE", foundStatus: "CLOSED"</code> | Checks preview rejects the supplied inactive report status. |
| UT-3-02-010 | <code>lostStatus: "RESOLVED", foundStatus: "ACTIVE"</code> | Checks preview rejects the supplied inactive report status. |

#### Preview Rejects Caller Who Owns Neither Report Before Database Access

[PreviewAndClaimMatchTests.cs:104](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L104)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-011 | <code>No parameters; the test body defines the fixture.</code> | Checks a caller owning neither report is rejected before match-repository access. |

#### Preview Rejects Empty Item Id Without Calling Item Service

[PreviewAndClaimMatchTests.cs:157](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L157)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-012 | <code>lostValid: False, foundValid: True</code> | Checks an empty report ID is rejected before an Item Service request. |
| UT-3-02-013 | <code>lostValid: True, foundValid: False</code> | Checks an empty report ID is rejected before an Item Service request. |

#### Preview Rejects Two Reports From The Same Owner

[PreviewAndClaimMatchTests.cs:120](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L120)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-014 | <code>No parameters; the test body defines the fixture.</code> | Checks two reports belonging to the same person cannot be previewed as a valid cross-user claim. |

#### Preview With No Photos Returns Both Reports And Normalizes Text Score To100

[PreviewAndClaimMatchTests.cs:32](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L32)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-015 | <code>No parameters; the test body defines the fixture.</code> | Checks a photo-free preview returns both reports and normalizes the available text/category score to 100%. |

#### Recalculating After Report Change Produces New Preview Version

[PreviewAndClaimMatchTests.cs:90](MatchingService.UnitTests/PreviewAndClaimMatch/PreviewAndClaimMatchTests.cs#L90)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-02-016 | <code>No parameters; the test body defines the fixture.</code> | Checks changing compared report data changes the preview version used for consistency checks. |

</details>

<a id="matchingservice-viewpotentialmatches"></a>

### View Potential Matches (19 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story ViewPotentialMatches -NoBuild`

<details><summary>All 19 individual cases in View Potential Matches (10 checks)</summary>

#### Current Api Exposes Certain Deactivated Matches In Dedicated Section

[ViewPotentialMatchesTests.cs:162](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L162)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-001 | <code>No parameters; the test body defines the fixture.</code> | Documents current API behaviour: certain deactivated matches remain visible in a dedicated section. This intentionally differs from the story requiring all deactivated matches to be hidden. |

#### Empty Match Page Returns An Empty Result And Zero Total

[ViewPotentialMatchesTests.cs:92](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L92)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-002 | <code>No parameters; the test body defines the fixture.</code> | Checks a caller with no returned matches receives an empty page and zero total. |

#### Invalid Section Or Page Is Rejected Before Repository Access

[ViewPotentialMatchesTests.cs:109](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L109)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-003 | <code>section: "active", page: 0, size: 20</code> | Checks invalid match-section or page arguments are rejected before querying matches. |
| UT-3-03-004 | <code>section: "active", page: 1, size: 101</code> | Checks invalid match-section or page arguments are rejected before querying matches. |
| UT-3-03-005 | <code>section: "unknown", page: 1, size: 20</code> | Checks invalid match-section or page arguments are rejected before querying matches. |

#### List Preserves Pagination And Scopes Repository Query To Caller

[ViewPotentialMatchesTests.cs:73](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L73)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-006 | <code>No parameters; the test body defines the fixture.</code> | Checks the match query carries the caller identity and expected pagination to the repository. |

#### Match Row Identifies Who Submitted The Claim

[ViewPotentialMatchesTests.cs:59](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L59)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-007 | <code>viewerIsClaimant: False, expectedLabel: "Someone claimed your item"</code> | Checks the match-row projection labels the claimant correctly for the current viewer. |
| UT-3-03-008 | <code>viewerIsClaimant: True, expectedLabel: "You claimed this item"</code> | Checks the match-row projection labels the claimant correctly for the current viewer. |

#### Match Row Maps Status To The Correct Viewer Section And Action State

[ViewPotentialMatchesTests.cs:19](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L19)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-009 | <code>status: "CONFIRMED", asLostReporter: True, section: "confirmed", canAct: False</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |
| UT-3-03-010 | <code>status: "FINDER_CONFIRMED", asLostReporter: False, section: "waiting-on-other", canAct: False</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |
| UT-3-03-011 | <code>status: "FINDER_CONFIRMED", asLostReporter: True, section: "waiting-on-you", canAct: True</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |
| UT-3-03-012 | <code>status: "LOST_REPORTER_CONFIRMED", asLostReporter: False, section: "waiting-on-you", canAct: True</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |
| UT-3-03-013 | <code>status: "LOST_REPORTER_CONFIRMED", asLostReporter: True, section: "waiting-on-other", canAct: False</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |
| UT-3-03-014 | <code>status: "REJECTED", asLostReporter: False, section: "rejected", canAct: False</code> | Checks each supplied status/viewer combination maps to its expected section and whether the viewer may act. |

#### Match Row Shows Counterparts Photo Title Category Date And Location

[ViewPotentialMatchesTests.cs:40](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L40)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-015 | <code>No parameters; the test body defines the fixture.</code> | Checks the row shows the other party item snapshot, including photo, title, category, date and location. |

#### Non Visible Match Cannot Be Opened By Owner

[ViewPotentialMatchesTests.cs:149](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L149)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-016 | <code>status: "AUTO_REJECTED_LOW_CONFIDENCE", active: True</code> | Checks an owner cannot open the supplied auto-rejected or inactive match through a non-visible review path. |
| UT-3-03-017 | <code>status: "FINDER_CONFIRMED", active: False</code> | Checks an owner cannot open the supplied auto-rejected or inactive match through a non-visible review path. |

#### Third Party Cannot Open Match Details

[ViewPotentialMatchesTests.cs:134](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L134)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-018 | <code>No parameters; the test body defines the fixture.</code> | Checks an unrelated user cannot open another pair of users match details. |

#### Unknown Match Returns404

[ViewPotentialMatchesTests.cs:120](MatchingService.UnitTests/ViewPotentialMatches/ViewPotentialMatchesTests.cs#L120)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-03-019 | <code>No parameters; the test body defines the fixture.</code> | Checks a missing match produces 404. |

</details>

<a id="matchingservice-lostreporterdecision"></a>

### Lost Reporter Decision (9 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story LostReporterDecision -NoBuild`

<details><summary>All 9 individual cases in Lost Reporter Decision (5 checks)</summary>

#### Confirmed Review Does Not Reveal Finder Contact In Details Response

[LostReporterDecisionTests.cs:66](MatchingService.UnitTests/LostReporterDecision/LostReporterDecisionTests.cs#L66)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-04-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the confirmed lost-reporter-facing details response excludes finder contact information; it does not test the separate contact endpoint. |

#### Decision And Contact Routes Require A Valid User Id

[LostReporterDecisionTests.cs:83](MatchingService.UnitTests/LostReporterDecision/LostReporterDecisionTests.cs#L83)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-04-002 | <code>action: "confirm"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |
| UT-3-04-003 | <code>action: "reject"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |
| UT-3-04-004 | <code>action: "return-contact"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |

#### Finder Confirmed Review Shows Found Item And Lets Lost Reporter Act

[LostReporterDecisionTests.cs:18](MatchingService.UnitTests/LostReporterDecision/LostReporterDecisionTests.cs#L18)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-04-005 | <code>No parameters; the test body defines the fixture.</code> | Checks the lost-reporter review shows the found item and enables actions at FINDER\_CONFIRMED. |

#### Other Decision States Do Not Offer Lost Reporter Actions

[LostReporterDecisionTests.cs:42](MatchingService.UnitTests/LostReporterDecision/LostReporterDecisionTests.cs#L42)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-04-006 | <code>status: "CONFIRMED"</code> | Checks the supplied out-of-turn or closed status does not offer lost-reporter actions in the review projection. |
| UT-3-04-007 | <code>status: "LOST_REPORTER_CONFIRMED"</code> | Checks the supplied out-of-turn or closed status does not offer lost-reporter actions in the review projection. |
| UT-3-04-008 | <code>status: "REJECTED"</code> | Checks the supplied out-of-turn or closed status does not offer lost-reporter actions in the review projection. |

#### Unrelated User Cannot Open Lost Reporter Review

[LostReporterDecisionTests.cs:55](MatchingService.UnitTests/LostReporterDecision/LostReporterDecisionTests.cs#L55)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-04-009 | <code>No parameters; the test body defines the fixture.</code> | Checks an unrelated user is rejected from the lost-reporter review. |

</details>

<a id="matchingservice-finderdecision"></a>

### Finder Decision (9 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story FinderDecision -NoBuild`

<details><summary>All 9 individual cases in Finder Decision (5 checks)</summary>

#### Confirmed Review Does Not Reveal Lost Reporter Contact In Details Response

[FinderDecisionTests.cs:66](MatchingService.UnitTests/FinderDecision/FinderDecisionTests.cs#L66)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-05-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the confirmed finder-facing details response excludes lost-reporter contact information; it does not test the separate contact endpoint. |

#### Decision And Contact Routes Require A Valid User Id

[FinderDecisionTests.cs:83](MatchingService.UnitTests/FinderDecision/FinderDecisionTests.cs#L83)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-05-002 | <code>action: "confirm"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |
| UT-3-05-003 | <code>action: "reject"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |
| UT-3-05-004 | <code>action: "return-contact"</code> | Checks decision/contact handlers reject the supplied missing or invalid caller identity. |

#### Lost Reporter Confirmed Review Shows Lost Item And Lets Finder Act

[FinderDecisionTests.cs:18](MatchingService.UnitTests/FinderDecision/FinderDecisionTests.cs#L18)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-05-005 | <code>No parameters; the test body defines the fixture.</code> | Checks the finder review shows the lost item and enables actions at LOST\_REPORTER\_CONFIRMED. |

#### Other Decision States Do Not Offer Finder Actions

[FinderDecisionTests.cs:42](MatchingService.UnitTests/FinderDecision/FinderDecisionTests.cs#L42)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-05-006 | <code>status: "CONFIRMED"</code> | Checks the supplied out-of-turn or closed status does not offer finder actions in the review projection. |
| UT-3-05-007 | <code>status: "FINDER_CONFIRMED"</code> | Checks the supplied out-of-turn or closed status does not offer finder actions in the review projection. |
| UT-3-05-008 | <code>status: "REJECTED"</code> | Checks the supplied out-of-turn or closed status does not offer finder actions in the review projection. |

#### Unrelated User Cannot Open Finder Review

[FinderDecisionTests.cs:55](MatchingService.UnitTests/FinderDecision/FinderDecisionTests.cs#L55)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-05-009 | <code>No parameters; the test body defines the fixture.</code> | Checks an unrelated user is rejected from the finder review. |

</details>

<a id="matchingservice-matchingnotifications"></a>

### Matching Notifications (9 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story MatchingNotifications -NoBuild`

<details><summary>All 9 individual cases in Matching Notifications (5 checks)</summary>

#### Invalid Recipient Is Rejected Before Building An Email

[MatchingNotificationTests.cs:56](MatchingService.UnitTests/MatchingNotifications/MatchingNotificationTests.cs#L56)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-06-001 | <code>No parameters; the test body defines the fixture.</code> | Checks malformed recipient input is rejected by notification construction. |

#### Production Frontend Link Must Use Https

[MatchingNotificationTests.cs:65](MatchingService.UnitTests/MatchingNotifications/MatchingNotificationTests.cs#L65)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-06-002 | <code>No parameters; the test body defines the fixture.</code> | Checks production notification links require HTTPS. |

#### Supported Outcome Builds The Right Email With Only Its App Link In Plain Text

[MatchingNotificationTests.cs:15](MatchingService.UnitTests/MatchingNotifications/MatchingNotificationTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-06-003 | <code>type: "COUNTERPART_ACTION", expectedSubject: "A claim was submitted for your report"</code> | Checks each supported outcome renders the intended plain-text notification and app link without contact or hidden matching data. |
| UT-3-06-004 | <code>type: "MATCH_CONFIRMED", expectedSubject: "Your match was confirmed"</code> | Checks each supported outcome renders the intended plain-text notification and app link without contact or hidden matching data. |
| UT-3-06-005 | <code>type: "MATCH_REJECTED", expectedSubject: "Your match was rejected"</code> | Checks each supported outcome renders the intended plain-text notification and app link without contact or hidden matching data. |

#### Supported Outcome Html Also Keeps Contact And Hidden Details Out Of The Email

[MatchingNotificationTests.cs:31](MatchingService.UnitTests/MatchingNotifications/MatchingNotificationTests.cs#L31)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-06-006 | <code>type: "COUNTERPART_ACTION"</code> | Checks each supported HTML template excludes contact and hidden matching details. |
| UT-3-06-007 | <code>type: "MATCH_CONFIRMED"</code> | Checks each supported HTML template excludes contact and hidden matching details. |
| UT-3-06-008 | <code>type: "MATCH_REJECTED"</code> | Checks each supported HTML template excludes contact and hidden matching details. |

#### Unknown Notification Type Is Not Sent As An Outcome Email

[MatchingNotificationTests.cs:47](MatchingService.UnitTests/MatchingNotifications/MatchingNotificationTests.cs#L47)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-06-009 | <code>No parameters; the test body defines the fixture.</code> | Checks an unsupported notification type is rejected rather than built as a valid outcome email. |

</details>

<a id="matchingservice-deactivatematches"></a>

### Deactivate Matches (10 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story DeactivateMatches -NoBuild`

<details><summary>All 10 individual cases in Deactivate Matches (6 checks)</summary>

#### Deactivated Match Review Is Read Only And Preserves The Audit Reason And Time

[DeactivateMatchesTests.cs:78](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L78)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the deactivated review is read-only and includes the retained deactivation reason/time. |

#### Default Matches Query Requires An Active Match

[DeactivateMatchesTests.cs:69](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L69)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-002 | <code>No parameters; the test body defines the fixture.</code> | Checks the default matches query contract requires active matches. |

#### Delete Event Rejects An Unknown Item Type

[DeactivateMatchesTests.cs:60](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L60)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-003 | <code>No parameters; the test body defines the fixture.</code> | Checks a delete-request event with an unsupported item type is rejected. |

#### Event Type Must Match Its Kafka Topic

[DeactivateMatchesTests.cs:42](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L42)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-004 | <code>topic: "items.found_item.resolved", eventType: "lost_item.resolved"</code> | Checks the item-event parser rejects event types inconsistent with the supplied Kafka topic. |
| UT-3-07-005 | <code>topic: "items.item.delete_requested", eventType: "lost_item.resolved"</code> | Checks the item-event parser rejects event types inconsistent with the supplied Kafka topic. |
| UT-3-07-006 | <code>topic: "items.lost_item.resolved", eventType: "found_item.resolved"</code> | Checks the item-event parser rejects event types inconsistent with the supplied Kafka topic. |

#### Lifecycle Event Is Mapped To The Correct Item And Reason

[DeactivateMatchesTests.cs:20](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L20)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-007 | <code>topic: "items.found_item.resolved", eventType: "found_item.resolved", expectedType: "FOUND", expectedReason: "RESOLVED"</code> | Checks resolve/delete payloads map to the correct item type, item ID and deactivation reason. |
| UT-3-07-008 | <code>topic: "items.item.delete_requested", eventType: "item.delete_requested", expectedType: "FOUND", expectedReason: "DELETED"</code> | Checks resolve/delete payloads map to the correct item type, item ID and deactivation reason. |
| UT-3-07-009 | <code>topic: "items.lost_item.resolved", eventType: "lost_item.resolved", expectedType: "LOST", expectedReason: "RESOLVED"</code> | Checks resolve/delete payloads map to the correct item type, item ID and deactivation reason. |

#### Lifecycle Event Requires The Item Identifier For Its Topic

[DeactivateMatchesTests.cs:51](MatchingService.UnitTests/DeactivateMatches/DeactivateMatchesTests.cs#L51)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-07-010 | <code>No parameters; the test body defines the fixture.</code> | Checks the item ID required by the supplied lifecycle topic must be present. |

</details>

<a id="matchingservice-reevaluatematchesonitemupdate"></a>

### Reevaluate Matches On Item Update (10 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story ReevaluateMatchesOnItemUpdate -NoBuild`

<details><summary>All 10 individual cases in Reevaluate Matches On Item Update (7 checks)</summary>

#### Corrected Report Uses The Same Scoring Engine And Can Clear The Threshold

[ReevaluateMatchesOnItemUpdateTests.cs:41](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L41)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-001 | <code>No parameters; the test body defines the fixture.</code> | Checks corrected item data is evaluated by the shared scoring engine and can rise above the threshold. |

#### Date And Location Edits Change The Snapshot But Not The Similarity Score

[ReevaluateMatchesOnItemUpdateTests.cs:63](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L63)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-002 | <code>No parameters; the test body defines the fixture.</code> | Checks edited date/location values change the snapshot/version data without changing the similarity score. |

#### Existing Pair With An Empty Item Id Cannot Be Scored

[ReevaluateMatchesOnItemUpdateTests.cs:118](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L118)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-003 | <code>No parameters; the test body defines the fixture.</code> | Checks scoring rejects an existing pair with an empty report identifier. |

#### Only One Report Has A Photo Text Scoring Completes Without Image Evidence

[ReevaluateMatchesOnItemUpdateTests.cs:80](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L80)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-004 | <code>No parameters; the test body defines the fixture.</code> | Checks re-scoring completes using available text evidence when only one report has a photo. |

#### Unsupported Item Events Are Rejected Before A Reevaluation Job Is Recorded

[ReevaluateMatchesOnItemUpdateTests.cs:99](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L99)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-005 | <code>topic: "items.found_item.deleted"</code> | Checks unsupported item-event topics do not record re-evaluation work. |
| UT-3-08-006 | <code>topic: "items.lost_item.resolved"</code> | Checks unsupported item-event topics do not record re-evaluation work. |
| UT-3-08-007 | <code>topic: "items.user.updated"</code> | Checks unsupported item-event topics do not record re-evaluation work. |

#### Updated Report That No Longer Matches Drops Below The Claim Threshold

[ReevaluateMatchesOnItemUpdateTests.cs:12](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L12)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-008 | <code>updateLost: False</code> | Checks an update making reports dissimilar reduces their score below the claim threshold. |
| UT-3-08-009 | <code>updateLost: True</code> | Checks an update making reports dissimilar reduces their score below the claim threshold. |

#### Update Event Missing Required Public Fields Is Rejected Before Persistence

[ReevaluateMatchesOnItemUpdateTests.cs:108](MatchingService.UnitTests/ReevaluateMatchesOnItemUpdate/ReevaluateMatchesOnItemUpdateTests.cs#L108)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-08-010 | <code>No parameters; the test body defines the fixture.</code> | Checks malformed update-event data is rejected before re-evaluation persistence. |

</details>

<a id="matchingservice-sendmatchappeal"></a>

### Send Match Appeal (16 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story SendMatchAppeal -NoBuild`

<details><summary>All 16 individual cases in Send Match Appeal (9 checks)</summary>

#### Appeal Endpoints Reject Requests Without A Valid User Id

[SendMatchAppealTests.cs:135](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L135)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-001 | <code>endpoint: "edit-warning"</code> | Checks appeal handlers reject missing/invalid caller IDs before processing the request. |
| UT-3-09-002 | <code>endpoint: "mine"</code> | Checks appeal handlers reject missing/invalid caller IDs before processing the request. |
| UT-3-09-003 | <code>endpoint: "pair-status"</code> | Checks appeal handlers reject missing/invalid caller IDs before processing the request. |
| UT-3-09-004 | <code>endpoint: "send"</code> | Checks appeal handlers reject missing/invalid caller IDs before processing the request. |

#### Edit Warning Rejects Invalid Report Type Before Database Access

[SendMatchAppealTests.cs:99](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L99)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-005 | <code>type: ""</code> | Checks an unsupported report type is rejected before looking up edit-warning information. |
| UT-3-09-006 | <code>type: "UNKNOWN"</code> | Checks an unsupported report type is rejected before looking up edit-warning information. |

#### Inactive Report Is Rejected With The Claim Flow Error

[SendMatchAppealTests.cs:82](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L82)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-007 | <code>lostStatus: "ACTIVE", foundStatus: "DELETED"</code> | Checks an appeal involving an inactive report returns the current claim-flow eligibility error. |
| UT-3-09-008 | <code>lostStatus: "RESOLVED", foundStatus: "ACTIVE"</code> | Checks an appeal involving an inactive report returns the current claim-flow eligibility error. |

#### Missing Preview Version Is Rejected Before Any Item Or Database Call

[SendMatchAppealTests.cs:39](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L39)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-009 | <code>version: "  "</code> | Checks missing or whitespace preview versions reject the appeal before any item/database call. |
| UT-3-09-010 | <code>version: ""</code> | Checks missing or whitespace preview versions reject the appeal before any item/database call. |

#### My Appeal Projection Shows Decision And Reason But Never The Admin Identity

[SendMatchAppealTests.cs:111](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L111)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-011 | <code>No parameters; the test body defines the fixture.</code> | Checks caller-facing appeal details include decision/reason fields while omitting the deciding admin identity. |

#### Overlong Note Is Rejected Before Any Item Or Database Call

[SendMatchAppealTests.cs:24](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L24)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-012 | <code>length: 301</code> | Checks an overlong appeal note is rejected before service or database access. |
| UT-3-09-013 | <code>length: 450</code> | Checks an overlong appeal note is rejected before service or database access. |

#### Send Policy Allows Only Authenticated Email Verified Users

[SendMatchAppealTests.cs:155](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L155)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-014 | <code>No parameters; the test body defines the fixture.</code> | Checks the real send-appeal policy accepts only authenticated, email-verified callers. |

#### User Owning Both Reports Is Rejected With The Claim Flow Error

[SendMatchAppealTests.cs:66](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L66)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-015 | <code>No parameters; the test body defines the fixture.</code> | Checks an appeal is rejected when the caller owns both reports. |

#### User Owning Neither Report Is Rejected With The Claim Flow Error

[SendMatchAppealTests.cs:52](MatchingService.UnitTests/SendMatchAppeal/SendMatchAppealTests.cs#L52)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-09-016 | <code>No parameters; the test body defines the fixture.</code> | Checks an appeal is rejected when the caller owns neither report. |

</details>

<a id="matchingservice-adminmatchappealreview"></a>

### Admin Match Appeal Review (19 cases)

**Run:** `.\test-unit.ps1 -Service MatchingService -Story AdminMatchAppealReview -NoBuild`

<details><summary>All 19 individual cases in Admin Match Appeal Review (8 checks)</summary>

#### Admin Appeal Projection Includes Score Breakdown Reason And Audit Fields

[AdminMatchAppealReviewTests.cs:82](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L82)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the admin-facing appeal projection includes score evidence, reason and audit fields. |

#### Admin Endpoints Reject Requests Without A Valid Admin Id

[AdminMatchAppealReviewTests.cs:110](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L110)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-002 | <code>endpoint: "list"</code> | Checks admin appeal handlers reject missing or invalid admin identity. |
| UT-3-10-003 | <code>endpoint: "open"</code> | Checks admin appeal handlers reject missing or invalid admin identity. |
| UT-3-10-004 | <code>endpoint: "reject"</code> | Checks admin appeal handlers reject missing or invalid admin identity. |
| UT-3-10-005 | <code>endpoint: "verify"</code> | Checks admin appeal handlers reject missing or invalid admin identity. |

#### Admin List Rejects Invalid Page Before Database Access

[AdminMatchAppealReviewTests.cs:145](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L145)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-006 | <code>page: 0</code> | Checks an invalid admin appeal-list page is rejected before a database call. |
| UT-3-10-007 | <code>page: 100001</code> | Checks an invalid admin appeal-list page is rejected before a database call. |

#### Admin Only Policy Rejects Ordinary Users And Accepts Admins

[AdminMatchAppealReviewTests.cs:130](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L130)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-008 | <code>No parameters; the test body defines the fixture.</code> | Checks the real admin-only authorization policy rejects regular users and accepts eligible admins. |

#### Decided Appeal Cannot Be Decided Again

[AdminMatchAppealReviewTests.cs:51](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L51)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-009 | <code>status: "REJECTED"</code> | Checks the current decision guard rejects an appeal whose decision has already been recorded. |
| UT-3-10-010 | <code>status: "VERIFIED"</code> | Checks the current decision guard rejects an appeal whose decision has already been recorded. |

#### List Rejects Unsupported Status Before Database Access

[AdminMatchAppealReviewTests.cs:23](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L23)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-011 | <code>status: ""</code> | Checks an unsupported appeal-list status filter is rejected before database access. |
| UT-3-10-012 | <code>status: "DELETED"</code> | Checks an unsupported appeal-list status filter is rejected before database access. |
| UT-3-10-013 | <code>status: "OPEN"</code> | Checks an unsupported appeal-list status filter is rejected before database access. |

#### Overlong Rejection Reason Is Rejected Before Any Database Call

[AdminMatchAppealReviewTests.cs:37](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L37)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-014 | <code>length: 301</code> | Checks an overlong admin rejection reason is rejected before database access. |
| UT-3-10-015 | <code>length: 450</code> | Checks an overlong admin rejection reason is rejected before database access. |

#### Verify Only Accepts Active Reports

[AdminMatchAppealReviewTests.cs:71](MatchingService.UnitTests/AdminMatchAppealReview/AdminMatchAppealReviewTests.cs#L71)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-3-10-016 | <code>status: "active", expected: True</code> | Checks the appeal-verification eligibility guard accepts active reports and rejects the supplied other statuses. |
| UT-3-10-017 | <code>status: "ACTIVE", expected: True</code> | Checks the appeal-verification eligibility guard accepts active reports and rejects the supplied other statuses. |
| UT-3-10-018 | <code>status: "DELETED", expected: False</code> | Checks the appeal-verification eligibility guard accepts active reports and rejects the supplied other statuses. |
| UT-3-10-019 | <code>status: "RESOLVED", expected: False</code> | Checks the appeal-verification eligibility guard accepts active reports and rejects the supplied other statuses. |

</details>

<a id="adminverifyservice-catalogue"></a>

## Admin Verify Service (96 cases)

| Story | Cases | Tested scope |
|---|---:|---|
| [Admin Listing And User Access](#adminverifyservice-adminlistinganduseraccess) | 19 | Admin item reads/deletes, event identity, user contact projection and endpoint authorization policies. |
| [Receive Listing Events](#adminverifyservice-receivelistingevents) | 15 | Listing-event identity/timestamps, malformed input, photo-edit identity and failed-write retry signalling. |
| [Flag Posting Bursts](#adminverifyservice-flagpostingbursts) | 13 | Collecting-window/status/cap rules, listing types, configuration changes and admin-alert contents. |
| [View Spam Records](#adminverifyservice-viewspamrecords) | 17 | Status-tab rules, sorting decisions, date/user-filter validation and admin access policy. |
| [Open Spam Record](#adminverifyservice-openspamrecord) | 8 | Open-endpoint admin policy and admin photo responses, including missing listings and empty photos. |
| [Dismiss Spam Record](#adminverifyservice-dismissspamrecord) | 4 | Dismiss-endpoint admin policy and the rule preventing dismissed records from collecting more listings. |
| [Solve Spam Record](#adminverifyservice-solvespamrecord) | 20 | The production frontend solve runner with mocked APIs: retries, skips, resume, kick outcomes and server-side validation/policy. |

<a id="adminverifyservice-adminlistinganduseraccess"></a>

### Admin Listing And User Access (19 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story AdminListingAndUserAccess -NoBuild`

<details><summary>All 19 individual cases in Admin Listing And User Access (7 checks)</summary>

#### Item Admin Read Delete And Photos All Carry The Admin Only Policy

[AdminAccessPolicyTests.cs:33](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminAccessPolicyTests.cs#L33)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-001 | <code>No parameters; the test body defines the fixture.</code> | Inspects admin read/delete/photo endpoint policy metadata for AdminOnly protection; no HTTP middleware is run. |

#### User Lookup Policy Requires An Active Admin Account

[AdminAccessPolicyTests.cs:17](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminAccessPolicyTests.cs#L17)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-002 | <code>admin: False, kicked: False, allowed: False</code> | Exercises the user-lookup authorization policy against the supplied admin/account eligibility cases. |
| UT-4-01-003 | <code>admin: True, kicked: False, allowed: True</code> | Exercises the user-lookup authorization policy against the supplied admin/account eligibility cases. |
| UT-4-01-004 | <code>admin: True, kicked: True, allowed: False</code> | Exercises the user-lookup authorization policy against the supplied admin/account eligibility cases. |

#### Admin Delete Publishes Owner Identity And Logs The Acting Admin

[AdminListingAndUserAccessTests.cs:46](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminListingAndUserAccessTests.cs#L46)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-005 | <code>type: FOUND</code> | Checks admin soft deletion publishes the original owner identity in the deletion event and records the acting admin for both item types. |
| UT-4-01-006 | <code>type: LOST</code> | Checks admin soft deletion publishes the original owner identity in the deletion event and records the acting admin for both item types. |

#### Admin Reads Listings In Every State

[AdminListingAndUserAccessTests.cs:26](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminListingAndUserAccessTests.cs#L26)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-007 | <code>type: FOUND, status: "ACTIVE", deleted: False</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |
| UT-4-01-008 | <code>type: FOUND, status: "ACTIVE", deleted: True</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |
| UT-4-01-009 | <code>type: FOUND, status: "RESOLVED", deleted: False</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |
| UT-4-01-010 | <code>type: LOST, status: "ACTIVE", deleted: False</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |
| UT-4-01-011 | <code>type: LOST, status: "ACTIVE", deleted: True</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |
| UT-4-01-012 | <code>type: LOST, status: "RESOLVED", deleted: False</code> | Checks admin read projections include the supplied active/resolved/deleted listing state and relevant fields. |

#### Filtered Out Deleted Or Unknown User Returns404

[AdminListingAndUserAccessTests.cs:115](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminListingAndUserAccessTests.cs#L115)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-013 | <code>No parameters; the test body defines the fixture.</code> | Checks an absent/filtered user lookup returns 404; deletion filtering is provided by the stubbed repository. |

#### Invalid Delete Does Not Publish An Event

[AdminListingAndUserAccessTests.cs:75](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminListingAndUserAccessTests.cs#L75)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-014 | <code>scenario: "deleted", expected: 404</code> | Checks the supplied missing/deleted/resolved delete case returns the expected status without requesting an event. |
| UT-4-01-015 | <code>scenario: "missing", expected: 404</code> | Checks the supplied missing/deleted/resolved delete case returns the expected status without requesting an event. |
| UT-4-01-016 | <code>scenario: "raced", expected: 409</code> | Checks the supplied missing/deleted/resolved delete case returns the expected status without requesting an event. |
| UT-4-01-017 | <code>scenario: "resolved", expected: 409</code> | Checks the supplied missing/deleted/resolved delete case returns the expected status without requesting an event. |

#### User Lookup Returns Only Live Contact Fields Including Kicked Users

[AdminListingAndUserAccessTests.cs:96](AdminVerifyService.UnitTests/AdminListingAndUserAccess/AdminListingAndUserAccessTests.cs#L96)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-01-018 | <code>kicked: False</code> | Checks normal and kicked user lookups expose only the expected live contact fields. |
| UT-4-01-019 | <code>kicked: True</code> | Checks normal and kicked user lookups expose only the expected live contact fields. |

</details>

<a id="adminverifyservice-receivelistingevents"></a>

### Receive Listing Events (15 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story ReceiveListingEvents -NoBuild`

<details><summary>All 15 individual cases in Receive Listing Events (5 checks)</summary>

#### Failed Database Write Rewinds The Message And Does Not Commit Its Offset

[FailedWriteRetryTests.cs:16](AdminVerifyService.UnitTests/ReceiveListingEvents/FailedWriteRetryTests.cs#L16)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-02-001 | <code>No parameters; the test body defines the fixture.</code> | Simulates a connection-factory failure, then checks the mocked Kafka consumer seeks back once and never commits that message offset. |

#### Intake Uses Listing Time And Preserves Its Identity

[ReceiveListingEventsTests.cs:13](AdminVerifyService.UnitTests/ReceiveListingEvents/ReceiveListingEventsTests.cs#L13)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-02-002 | <code>topic: "items.found_item.created", type: "FOUND", update: False</code> | Checks lost/found created/updated event parsing preserves event/listing/user IDs, selects created or updated time, and omits title/description from tracked data. |
| UT-4-02-003 | <code>topic: "items.found_item.updated", type: "FOUND", update: True</code> | Checks lost/found created/updated event parsing preserves event/listing/user IDs, selects created or updated time, and omits title/description from tracked data. |
| UT-4-02-004 | <code>topic: "items.lost_item.created", type: "LOST", update: False</code> | Checks lost/found created/updated event parsing preserves event/listing/user IDs, selects created or updated time, and omits title/description from tracked data. |
| UT-4-02-005 | <code>topic: "items.lost_item.updated", type: "LOST", update: True</code> | Checks lost/found created/updated event parsing preserves event/listing/user IDs, selects created or updated time, and omits title/description from tracked data. |

#### Invalid Payload Does Not Produce A Listing

[ReceiveListingEventsTests.cs:45](AdminVerifyService.UnitTests/ReceiveListingEvents/ReceiveListingEventsTests.cs#L45)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-02-006 | <code>json: ""</code> | Checks null, empty, malformed or incomplete JSON cannot produce a tracked listing. |
| UT-4-02-007 | <code>json: "{"</code> | Checks null, empty, malformed or incomplete JSON cannot produce a tracked listing. |
| UT-4-02-008 | <code>json: "{}"</code> | Checks null, empty, malformed or incomplete JSON cannot produce a tracked listing. |
| UT-4-02-009 | <code>json: "null"</code> | Checks null, empty, malformed or incomplete JSON cannot produce a tracked listing. |
| UT-4-02-010 | <code>json: null</code> | Checks null, empty, malformed or incomplete JSON cannot produce a tracked listing. |

#### Missing Identity Or Post Time Is Rejected

[ReceiveListingEventsTests.cs:53](AdminVerifyService.UnitTests/ReceiveListingEvents/ReceiveListingEventsTests.cs#L53)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-02-011 | <code>missing: "createdAt"</code> | Checks removal of the named required identity/time field makes event parsing reject the payload. |
| UT-4-02-012 | <code>missing: "eventId"</code> | Checks removal of the named required identity/time field makes event parsing reject the payload. |
| UT-4-02-013 | <code>missing: "lostItemId"</code> | Checks removal of the named required identity/time field makes event parsing reject the payload. |
| UT-4-02-014 | <code>missing: "userId"</code> | Checks removal of the named required identity/time field makes event parsing reject the payload. |

#### Photo Only Edit Uses The Same Listing Identity

[ReceiveListingEventsTests.cs:65](AdminVerifyService.UnitTests/ReceiveListingEvents/ReceiveListingEventsTests.cs#L65)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-02-015 | <code>No parameters; the test body defines the fixture.</code> | Checks a photo-only update keeps the listing/user identity while receiving a distinct event ID; it does not prove database post-count deduplication. |

</details>

<a id="adminverifyservice-flagpostingbursts"></a>

### Flag Posting Bursts (13 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story FlagPostingBursts -NoBuild`

<details><summary>All 13 individual cases in Flag Posting Bursts (4 checks)</summary>

#### Changed Configuration Changes The Next Cap Decision

[FlagPostingBurstsTests.cs:46](AdminVerifyService.UnitTests/FlagPostingBursts/FlagPostingBurstsTests.cs#L46)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-03-001 | <code>No parameters; the test body defines the fixture.</code> | Changes the in-memory optional cap from off to three and checks the next collection decision changes without a code change. |

#### Collecting Rule Accepts Both Listing Types

[FlagPostingBurstsTests.cs:37](AdminVerifyService.UnitTests/FlagPostingBursts/FlagPostingBurstsTests.cs#L37)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-03-002 | <code>type: "FOUND"</code> | Checks the collection predicate accepts both lost and found listing types for an eligible open record. |
| UT-4-03-003 | <code>type: "LOST"</code> | Checks the collection predicate accepts both lost and found listing types for an eligible open record. |

#### Collecting Rule Honors Exact Window Status And Optional Cap

[FlagPostingBurstsTests.cs:21](AdminVerifyService.UnitTests/FlagPostingBursts/FlagPostingBurstsTests.cs#L21)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-03-004 | <code>status: "DISMISSED", secondsFromExpiry: -1, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-005 | <code>status: "NEEDS_REVIEW", secondsFromExpiry: -1, count: 3, cap: 0, expected: True</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-006 | <code>status: "NEEDS_REVIEW", secondsFromExpiry: -1, count: 3, cap: 3, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-007 | <code>status: "NEEDS_REVIEW", secondsFromExpiry: -1, count: 3, cap: 4, expected: True</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-008 | <code>status: "NEEDS_REVIEW", secondsFromExpiry: 0, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-009 | <code>status: "NEEDS_REVIEW", secondsFromExpiry: 1, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-010 | <code>status: "PENDING_SOLVE", secondsFromExpiry: -1, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-011 | <code>status: "SOLVED", secondsFromExpiry: -1, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |
| UT-4-03-012 | <code>status: "UNDER_REVIEW", secondsFromExpiry: -1, count: 3, cap: 0, expected: False</code> | Checks the collection predicate against exact expiry boundaries, review status, listing count and optional cap; expected is the required boolean result. |

#### New Record Alert Contains Review Link And No Listing Content

[SpamAlertTests.cs:9](AdminVerifyService.UnitTests/FlagPostingBursts/SpamAlertTests.cs#L9)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-03-013 | <code>No parameters; the test body defines the fixture.</code> | Checks the admin-alert template includes the review link and omits listing title/description; SMTP delivery is not attempted. |

</details>

<a id="adminverifyservice-viewspamrecords"></a>

### View Spam Records (17 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story ViewSpamRecords -NoBuild`

<details><summary>All 17 individual cases in View Spam Records (5 checks)</summary>

#### Date Filter Includes The Whole End Date And Deduplicates Users

[ViewSpamRecordsTests.cs:26](AdminVerifyService.UnitTests/ViewSpamRecords/ViewSpamRecordsTests.cs#L26)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-04-001 | <code>No parameters; the test body defines the fixture.</code> | Checks filter parsing includes the whole end date and removes duplicate user IDs. |

#### Invalid Filters Return400

[ViewSpamRecordsTests.cs:44](AdminVerifyService.UnitTests/ViewSpamRecords/ViewSpamRecordsTests.cs#L44)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-04-002 | <code>tab: "active", sort: "score", from: "2026-10-04", to: "2026-10-01", users: null, page: 1</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-003 | <code>tab: "active", sort: "score", from: "bad-date", to: null, users: null, page: 1</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-004 | <code>tab: "active", sort: "score", from: null, to: null, users: "bad-user-id", page: 1</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-005 | <code>tab: "active", sort: "score", from: null, to: null, users: null, page: 0</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-006 | <code>tab: "active", sort: "score", from: null, to: null, users: null, page: 100001</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-007 | <code>tab: "active", sort: "wrong", from: null, to: null, users: null, page: 1</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |
| UT-4-04-008 | <code>tab: "wrong", sort: "score", from: null, to: null, users: null, page: 1</code> | Checks the supplied invalid tab/sort/date/user/page combination is rejected with 400 before listing records. |

#### List Requires Admin

[ViewSpamRecordsTests.cs:54](AdminVerifyService.UnitTests/ViewSpamRecords/ViewSpamRecordsTests.cs#L54)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-04-009 | <code>authenticated: False, claim: null, expected: False</code> | Runs the spam-list authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-04-010 | <code>authenticated: True, claim: "0", expected: False</code> | Runs the spam-list authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-04-011 | <code>authenticated: True, claim: "1", expected: True</code> | Runs the spam-list authorization policy for the supplied authentication/admin-claim combination. |

#### Server Sorting Uses Score By Default

[ViewSpamRecordsTests.cs:22](AdminVerifyService.UnitTests/ViewSpamRecords/ViewSpamRecordsTests.cs#L22)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-04-012 | <code>sort: "date", score: False</code> | Checks the sort-option decision uses score by default and the expected alternative for date ordering; no SQL ordering is executed. |
| UT-4-04-013 | <code>sort: "score", score: True</code> | Checks the sort-option decision uses score by default and the expected alternative for date ordering; no SQL ordering is executed. |
| UT-4-04-014 | <code>sort: null, score: True</code> | Checks the sort-option decision uses score by default and the expected alternative for date ordering; no SQL ordering is executed. |

#### Tabs Map To Their Server Statuses

[ViewSpamRecordsTests.cs:12](AdminVerifyService.UnitTests/ViewSpamRecords/ViewSpamRecordsTests.cs#L12)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-04-015 | <code>tab: "active", expected: "NEEDS_REVIEW,UNDER_REVIEW,PENDING_SOLVE"</code> | Checks the supplied UI tab maps to the exact set of server statuses. |
| UT-4-04-016 | <code>tab: "dismissed", expected: "DISMISSED"</code> | Checks the supplied UI tab maps to the exact set of server statuses. |
| UT-4-04-017 | <code>tab: "solved", expected: "SOLVED"</code> | Checks the supplied UI tab maps to the exact set of server statuses. |

</details>

<a id="adminverifyservice-openspamrecord"></a>

### Open Spam Record (8 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story OpenSpamRecord -NoBuild`

<details><summary>All 8 individual cases in Open Spam Record (3 checks)</summary>

#### Admin Photos Return Repository Order Including An Empty Collection

[OpenSpamRecordTests.cs:16](AdminVerifyService.UnitTests/OpenSpamRecord/OpenSpamRecordTests.cs#L16)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-05-001 | <code>type: FOUND, count: 0</code> | Checks photo projection preserves repository order and handles an empty collection for an existing listing. |
| UT-4-05-002 | <code>type: FOUND, count: 2</code> | Checks photo projection preserves repository order and handles an empty collection for an existing listing. |
| UT-4-05-003 | <code>type: LOST, count: 0</code> | Checks photo projection preserves repository order and handles an empty collection for an existing listing. |
| UT-4-05-004 | <code>type: LOST, count: 2</code> | Checks photo projection preserves repository order and handles an empty collection for an existing listing. |

#### Open Requires Admin

[OpenSpamRecordTests.cs:44](AdminVerifyService.UnitTests/OpenSpamRecord/OpenSpamRecordTests.cs#L44)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-05-005 | <code>authenticated: False, claim: null, expected: False</code> | Runs the open-record authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-05-006 | <code>authenticated: True, claim: "0", expected: False</code> | Runs the open-record authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-05-007 | <code>authenticated: True, claim: "1", expected: True</code> | Runs the open-record authorization policy for the supplied authentication/admin-claim combination. |

#### Unknown Listing Returns404 Without Fetching Photos

[OpenSpamRecordTests.cs:31](AdminVerifyService.UnitTests/OpenSpamRecord/OpenSpamRecordTests.cs#L31)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-05-008 | <code>No parameters; the test body defines the fixture.</code> | Checks an unknown admin listing returns 404 before attempting to fetch its photos. |

</details>

<a id="adminverifyservice-dismissspamrecord"></a>

### Dismiss Spam Record (4 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story DismissSpamRecord -NoBuild`

<details><summary>All 4 individual cases in Dismiss Spam Record (2 checks)</summary>

#### Dismissed Record Cannot Collect More Listings

[DismissSpamRecordTests.cs:19](AdminVerifyService.UnitTests/DismissSpamRecord/DismissSpamRecordTests.cs#L19)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-06-001 | <code>No parameters; the test body defines the fixture.</code> | Checks the collection rule refuses additions to a dismissed spam record; it does not execute a dismissal transaction. |

#### Dismiss Requires Admin

[DismissSpamRecordTests.cs:15](AdminVerifyService.UnitTests/DismissSpamRecord/DismissSpamRecordTests.cs#L15)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-06-002 | <code>authenticated: False, claim: null, expected: False</code> | Runs the dismiss authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-06-003 | <code>authenticated: True, claim: "0", expected: False</code> | Runs the dismiss authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-06-004 | <code>authenticated: True, claim: "1", expected: True</code> | Runs the dismiss authorization policy for the supplied authentication/admin-claim combination. |

</details>

<a id="adminverifyservice-solvespamrecord"></a>

### Solve Spam Record (20 cases)

**Run:** `.\test-unit.ps1 -Service AdminVerifyService -Story SolveSpamRecord -NoBuild`

<details><summary>All 20 individual cases in Solve Spam Record (3 checks)</summary>

#### Invalid Listing Result Is Rejected Before Database Access

[SolveSpamRecordTests.cs:55](AdminVerifyService.UnitTests/SolveSpamRecord/SolveSpamRecordTests.cs#L55)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-07-001 | <code>result: "UNKNOWN"</code> | Checks an absent/unsupported solve-result value returns a validation error before a database call. |
| UT-4-07-002 | <code>result: null</code> | Checks an absent/unsupported solve-result value returns a validation error before a database call. |

#### Production Solve Runner Honors Api Results And Retry Policy

[SolveSpamRecordTests.cs:25](AdminVerifyService.UnitTests/SolveSpamRecord/SolveSpamRecordTests.cs#L25)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-07-003 | <code>scenario: "already-kicked"</code> | An already-kicked API response is treated as KICKED without an error. |
| UT-4-07-004 | <code>scenario: "failed-delete-still-kicks"</code> | A requested kick still occurs once after five failed deletes, while solve finalization remains blocked. |
| UT-4-07-005 | <code>scenario: "finish-failure"</code> | A finalization error stops the solve and exposes the supplied error message. |
| UT-4-07-006 | <code>scenario: "five-attempt-limit"</code> | Exactly five failed deletes, four recorded 1,000 ms retry delays, a saved FAILED result, stopped state and no finish call. |
| UT-4-07-007 | <code>scenario: "kick-checked"</code> | One kick follows the listing-result save and its result is saved as KICKED. |
| UT-4-07-008 | <code>scenario: "kick-not-repeated"</code> | Resume does not repeat a kick or its saved result after an earlier attempt. |
| UT-4-07-009 | <code>scenario: "kick-refused"</code> | A refused kick is saved as FAILED and the message directs the admin to User Management. |
| UT-4-07-010 | <code>scenario: "kick-unchecked"</code> | No kick or kick-result write is requested when kicking is not selected. |
| UT-4-07-011 | <code>scenario: "resume"</code> | Previously DELETED/SKIPPED listings are ignored and only the failed listing is retried with the resume flag. |
| UT-4-07-012 | <code>scenario: "retry-succeeds"</code> | Two failures followed by success produce three delete calls, two fake delays and a saved DELETED result. |
| UT-4-07-013 | <code>scenario: "save-result-failure"</code> | A listing-result save failure stops the solve and prevents finalization. |
| UT-4-07-014 | <code>scenario: "skip-404"</code> | A 404 delete response is saved as SKIPPED after one call, with no retry delay. |
| UT-4-07-015 | <code>scenario: "skip-409"</code> | A 409 delete response is saved as SKIPPED after one call, with no retry delay. |
| UT-4-07-016 | <code>scenario: "start-forbidden"</code> | A 403 start response stops before deletion and preserves the Forbidden message. |
| UT-4-07-017 | <code>scenario: "success"</code> | One successful delete is saved as DELETED and the solve state reaches solved with no errors. |

#### Solve Requires Admin

[SolveSpamRecordTests.cs:66](AdminVerifyService.UnitTests/SolveSpamRecord/SolveSpamRecordTests.cs#L66)

| Case ID | Exact inputs / expected values | What this case checks |
|---|---|---|
| UT-4-07-018 | <code>authenticated: False, claim: null, expected: False</code> | Runs the solve authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-07-019 | <code>authenticated: True, claim: "0", expected: False</code> | Runs the solve authorization policy for the supplied authentication/admin-claim combination. |
| UT-4-07-020 | <code>authenticated: True, claim: "1", expected: True</code> | Runs the solve authorization policy for the supplied authentication/admin-claim combination. |

</details>

## Reporting checks

Seven additional tooling checks, separate from the 523 application cases:

```powershell
node --test testing-pipeline/unit-testing/scripts/summarize-results.test.mjs
```

| Reporting check | What it verifies |
|---|---|
| Service/story/class/method grouping | Parameterized cases retain their individual counts, identifiers and parameters; successful console output stays compact. |
| Same method name in different classes | Separate test classes are not accidentally combined into one check. |
| Failure evidence | Exact inputs, full messages and stack traces are preserved; failures open prominently and HTML/table characters are escaped. |
| Skipped cases | Skipped cases are counted separately from executed cases and retain their reason. |
| Incomplete evidence | Missing, truncated, inconsistent and all-skipped TRX inputs cannot yield a passing report. |
| Partial passing TRX after command failure | A failed build/test command cannot be hidden by the available passing subset of results. |
| Runtime/scope reporting | Actual elapsed time is separate from summed case time, and the selected scope is escaped safely. |

Eight additional demo-runner checks cover selection/order, service-only pause budgeting, single-service/story runs, slow execution, failure evidence, prepared-assembly selection, unindented case output and refusal to reuse results:

```powershell
node --test testing-pipeline/unit-testing/scripts/demo-runner.test.mjs
```
