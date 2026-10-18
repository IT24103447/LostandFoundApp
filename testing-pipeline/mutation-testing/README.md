# Mutation testing

Mutation testing measures how well the unit test suites detect real faults.
Stryker.NET 4.0.0 introduces small changes into each service's story-scoped
source code, runs the unit tests against every changed version, and counts
mutants killed by the tests. A mutant that survives means an existing test
would not catch that fault; a mutant no test reaches is reported as a
no-coverage test gap. Scores are therefore stronger evidence than line
coverage alone.

## How scoring works

- Each service runs its own Stryker.NET run against its own unit test project.
  Scores are never blended across services, so a strong service cannot carry a
  weak one.
- Score definition: killed / (killed + survived + timeout). Mutants in code
  paths no test reaches are reported as no-coverage test gaps, not un-killed.
  Both "Score" and "Score with gaps" columns appear below.
- Each service is gated on its own break-at threshold, set just below its
  measured baseline (ratchet rule). A later score may not drop below it;
  improvements only ever raise it.
- The report parser in run.sh is the single gate. Stryker's own thresholds are
  not used because Stryker.NET 4.x counts no-coverage mutants differently.

## Results

Validated locally on 18 October 2026 (full run exit 0, status.txt: passed):

| Service | Mutants | Killed | Survived | No coverage | Score | Score with gaps | Break-at |
|---|---|---|---|---|---|---|---|
| AuthService | 460 | 162 | 37 | 40 | 81% | 68% | 75 |
| ItemService | 810 | 303 | 156 | 138 | 66% | 51% | 60 |
| MatchingService | 1010 | 323 | 183 | 291 | 64% | 41% | 60 |
| AdminVerifyService | 430 | 103 | 36 | 185 | 74% | 32% | 70 |

- Total mutants (in scope): 2,710
- Mutants tested: 1,303
- Killed: 891
- Survived: 412
- No coverage (test gaps): 654

Negative-gate verification: raising one service's break-at above its measured
baseline fails only that service (BELOW THRESHOLD) while the other three stay
PASSED. Gates are independent per service.

## Services and stories

Four services are covered, one Stryker.NET run each. Scoped source files are
the files the stories exercise.

### AuthService

Run: auth-service | Unit project: AuthService.UnitTests

| Story  | What is covered |
|---|---|
| Registration | Register, verify email, login |
| EmailVerification | Resend verification, code validation |
| Login | User/admin JWT responses, cookie behaviour |
| Profile | Read/update profile |
| PasswordReset | Forgot/reset password, OTP lifecycle |
| AccountDeletion | Delete account, soft delete |
| AdminUserManagement | List users, search, kick, ban |

Scope: 16 files - `AuthController.cs`, `AdminController.cs`,
`PasswordValidator.cs`, and the request/response DTOs their validation tests
exercise (`RegisterRequest.cs`, `LoginRequest.cs`, `LoginResponse.cs`,
`ConfirmRequest.cs`, `VerifyEmailRequest.cs`, `ResendVerificationRequest.cs`,
`ForgotPasswordRequest.cs`, `ResetPasswordRequest.cs`,
`UpdateProfileRequest.cs`, `UserProfileDto.cs`, `DeleteAccountRequest.cs`,
`AdminUserDto.cs`, `AdminUserContactDto.cs`).

### ItemService

Run: item-service | Unit project: ItemService.UnitTests

| Story  | What is covered |
|---|---|
| ReportLostItem | Report lost item |
| ReportFoundItem | Report found item |
| EditItemReport | Edit item report |
| SearchAndFilterItems | Search and filter items |
| MarkResolved | Mark item resolved |
| ViewItemDetails | View item details |

Scope: 16 files - `LostItemsController.cs`, `FoundItemsController.cs`,
`ItemsController.cs`, and the DTOs their validation tests exercise
(`ReportLostItemRequest.cs`, `ReportFoundItemRequest.cs`,
`UpdateLostItemRequest.cs`, `UpdateFoundItemRequest.cs`, `ItemDetailDto.cs`,
`ItemSummaryDto.cs`, `PagedResultDto.cs`).

### MatchingService

Run: matching-service | Unit project: MatchingService.UnitTests

| Story  | What is covered |
|---|---|
| ImageDescriptionPipeline | Photo description pipeline |
| PreviewAndClaimMatch | Preview and claim match |
| ViewPotentialMatches | View potential matches |
| LostReporterDecision | Lost reporter decision |
| FinderDecision | Finder decision |
| MatchingNotifications | Match email sender |
| MatchDeactivation | Match deactivation |
| MatchReevaluation | Match re-evaluation |
| SendMatchAppeal | Send match appeal |
| AdminMatchAppealReview | Admin match appeal review |

Scope: 18 files - `ImageDescriptionWorker.cs`, `ImageDescriptionValidator.cs`,
`BlobUrlPhotoKeyGenerator.cs`, `ClaimService.cs`, `ClaimItemClient.cs`,
`ClaimModels.cs`, `MatchReadModels.cs`, `MatchReadService.cs`,
`MatchQueriesController.cs`, `FinderDecisionsController.cs`,
`LostReporterDecisionsController.cs`, `MatchEmailSender.cs`,
`MatchReevaluationEventHandler.cs`, `AppealService.cs`, `AdminAppealService.cs`,
`AppealModels.cs`, `AppealsController.cs`, `AdminAppealsController.cs`.

### AdminVerifyService

Run: adminverify-service | Unit project: AdminVerifyService.UnitTests

| Story  | What is covered |
|---|---|
| AdminListingAndUserAccess | Admin listing and user access |
| OpenSpamRecord | Open spam record |
| DismissSpamRecord | Dismiss spam record |
| FlagPostingBursts | Flag posting bursts |
| SpamAlert | Spam alert |
| ReceiveListingEvents | Receive listing events |
| SolveSpamRecord | Solve spam record |
| ViewSpamRecords | View spam records |

Scope: 11 files - `SpamRule.cs`, `SpamRecordRepository.cs`,
`SpamAlertEmailSender.cs`, `SpamAlertRepository.cs`, `SpamSolveRepository.cs`,
`SpamReviewQueryParser.cs`, `SpamReviewModels.cs`, `ListingEventParser.cs`,
`ListingEventConsumer.cs`, `ListingEventHandler.cs`, `SecurityRegistration.cs`.

The AdminVerifyService test project references three service projects, so
run.sh passes `--project services/AdminVerifyService/AdminVerifyService.csproj`
to tell Stryker.NET which project under test to mutate. Paths are resolved to
native form at run time (cygpath on Git Bash; unchanged on CI runners), so the
same command works on Windows and Linux.


## Run locally

Prerequisites: .NET 8 SDK, Node.js, and Git Bash on Windows. From the
repository root:

```
export ASE_REPORT_ROOT="$(mktemp -d)"
bash testing-pipeline/mutation-testing/run.sh
```

`ASE_REPORT_ROOT` must not point at a directory used by an earlier run
(`status.txt` exists); choose a fresh temporary directory each time.

The command restores/builds each unit test project, restores the pinned local
`dotnet-stryker` 4.0.0 tool (`.config/dotnet-tools.json`), runs one Stryker.NET
run per service, then merges the four JSON reports into one category report.

Outputs under `$ASE_REPORT_DIR/mutation-testing/`:
- `status.txt` - passed/failed/not-implemented
- `console.log` - full execution output
- `summary.md` - scope, counts, per-service results, per-file table
- `details.json` - individual mutant descriptions and outcomes
- `stryker-report/<service>/` - per-service Stryker HTML/JSON reports

On Windows use PowerShell or Git Bash from the same repository directory for
every invocation; Windows and CI runners each need their own restore/build
outputs.

