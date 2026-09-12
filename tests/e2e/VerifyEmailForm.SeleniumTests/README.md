# VerifyEmailForm.SeleniumTests — setup instructions

Selenium tests against your real verify-email screen's UI — checking what the
**user sees** (inline errors, success messages, redirects, cooldown timers),
unlike the xUnit tests (`VerificationFlowTests.cs`) which check what the
**API returns**.

| Scenario | Covered by |
|---|---|
| 1 – Successful Verification | `VerifyEmail_CorrectCode_ShowsSuccess_AndRedirectsHome` *(needs Mailtrap — see below)* |
| 2 – Incorrect OTP | `VerifyEmail_IncorrectCode_ShowsError_ClearsBoxes_AllowsRetry` |
| 3 – Too Many Failed Attempts | `VerifyEmail_TooManyWrongAttempts_LocksOtp_PromptsResend` |
| 4 – Expired OTP | *not covered here — see note below* |
| 5 – Resend OTP | `Resend_ValidEmail_ShowsConfirmation_AndStartsCooldown`, `Resend_InvalidEmail_ShowsValidationError_DoesNotSend` |
| 6 – Posting Restriction Lifted | *not covered here — see note below* |
| (extra) No/expired session | `VerifyEmail_NoSessionToken_ShowsSessionExpiredPrompt` |

## Before running

Same as `RegisterForm.SeleniumTests` — these drive a real browser against the
real app:

1. Backend running: `dotnet run` in `services/AuthService` (MySQL + Kafka containers up)
2. Frontend running: `npm run dev` in `frontend/`
3. Google Chrome installed on this PC (any recent version — the test project
   downloads the matching driver automatically the first time you run it)

## Where to put this folder

```
LostandFoundApp/
  services/
    AuthService/
    AuthService.Tests/
  tests/
    e2e/
      RegisterForm.SeleniumTests/
      VerifyEmailForm.SeleniumTests/   <-- this folder
```

## Run it

From inside `VerifyEmailForm.SeleniumTests`:

```
dotnet restore
dotnet test
```

## Getting Scenario 1 to actually run (Mailtrap setup)

Every other test avoids needing the real OTP by only exercising *wrong*-code
paths (which is most of what makes this story tricky to get right). But
Scenario 1 — the actual happy path — needs the real 6-digit code the backend
emailed, and that code is never returned by any API response (correctly —
returning it would defeat the point of email verification).

To make this test real instead of a stub, it reads the code straight out of
the Mailtrap sandbox inbox the backend is already configured to send to in
Development (`services/AuthService/appsettings.Development.json` → `Smtp`).
Set three environment variables before running:

```
MAILTRAP_API_TOKEN=<Settings → API Tokens in your Mailtrap account>
MAILTRAP_ACCOUNT_ID=<numeric account id, visible in the Mailtrap URL>
MAILTRAP_INBOX_ID=<numeric inbox id, visible in the inbox's URL/SMTP settings page>
```

If these aren't set, `VerifyEmail_CorrectCode_ShowsSuccess_AndRedirectsHome`
prints a note and passes trivially instead of failing — so the rest of the
suite still runs fine without a Mailtrap account. See `MailtrapClient.cs`.

## If everything times out immediately

If every test fails with `NoSuchElementException` looking for `#email` or
`h1` — even the ones that don't touch the backend at all — the browser
almost certainly landed on a page that hadn't finished loading yet, not a
real bug in the form. The usual cause on Windows: Vite's dev server compiles
each route's modules on demand the first time it's requested, which can take
longer than you'd expect (worse with antivirus scanning each file as it's
written). Two fixes:

1. **Pre-warm the dev server** — before running the tests, manually open
   `http://localhost:5173/register` and `http://localhost:5173/verify-email`
   once in a normal Chrome window each, then re-run `dotnet test`.
2. The wait timeout is already set generously (20s, 30s for the
   registration step) to absorb this — if it's still not enough on your
   machine, bump `TimeSpan.FromSeconds(20)` in the constructor higher.

## Notes / known gaps

- **Scenario 3 asserts against 10 wrong attempts, not 5.** The story's AC
  says the OTP locks "on the 6th" wrong attempt. The backend's actual
  configured value (`services/AuthService/appsettings.json` →
  `Auth:MaxOtpAttempts`) is `10` — this is the same AC-vs-config mismatch
  already flagged in `VerificationFlowTests.cs` (DEF-05), just re-surfaced
  here at the UI level. This test is written against the real, running
  behavior so it stays accurate; once that config is fixed to `5`, update
  the `maxOtpAttempts` constant at the top of the test.
- **Registering repeatedly across test runs can trip the register rate limit
  too.** `/api/auth/register` is limited to 5 requests/minute per caller
  (`Program.cs`), and that counter lives in the backend process's memory —
  it does **not** reset when you re-run `dotnet test`, only when you restart
  the backend itself. If you're iterating quickly (running the suite
  several times in a row while debugging), you can exhaust that budget and
  see a registration silently fail to redirect (the wait for `/verify-email`
  just times out) even though nothing is actually broken. If a test fails
  that way, restart `dotnet run` in `AuthService` and try again.
- **Scenario 3's fixed-window rate limiter is shared across all tests.**
  `/api/auth/verify-email` is limited to 10 requests/minute per caller
  (`Program.cs`), which is exactly what this test needs to hit the lockout —
  but if you run the whole suite back-to-back inside the same minute, other
  tests hitting the same endpoint could occasionally cause a stray 429 and a
  flaky failure. If you see that, re-run just this test in isolation.
- **Scenario 4 (Expired OTP) isn't practical to test at the UI level** — the
  real expiry window is 10 minutes (`Auth:OtpExpiryMinutes`), and there's no
  test-only way to fast-forward it from outside the process. It's already
  covered at the unit level by `VerificationFlowTests.VerifyEmail_NoActiveToken_ReturnsExpiryError`,
  which simulates the same "no active token" state the controller actually
  sees for an expired code.
- **Scenario 6 (posting restriction lifted) isn't testable yet** — there's
  no item-posting UI in this codebase yet (`services/ItemService` is still
  just a `.gitkeep`), so there's nothing to click through to prove the
  restriction is lifted. Once that UI exists, add a test here that logs in
  post-verification and confirms the "create report" action is enabled.
- **This suite fixes routing that has drifted since `RegisterForm.SeleniumTests`
  was written**: that project's comment claims the registration form is
  mounted at `/`, but the current `App.tsx` has `/` as `HomePage` and the
  form at `/register`. This suite's `RegisterNewUserAndReachVerifyEmail()`
  helper uses the correct current route (`/register`) — worth fixing
  `RegisterForm.SeleniumTests` to match, since as written it would fail
  against this build of the frontend.
