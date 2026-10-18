# UI / E2E testing

Selenium WebDriver browser tests for the Lost & Found app. They drive the real React frontend in Chrome and, through it, the Auth, Item, Matching and Admin Verify services. This folder is the UI/E2E category of the ASE testing pipeline. Shared setup, the CI workflow and the reporting contract are described in [`../README.md`](../README.md).

> **Status:** complete. The suite has 71 cases and the category runs for real (`ASE_SUITE_READY=true`). On GitHub Actions the suite passed 71 of 71 in headless Chrome, with a wall-clock time of 183 s. The Demo subset (13 tests) adds up to about 33 s of test time in CI.

## What is tested

- **Tools:** .NET 8, Selenium WebDriver 4.25, xUnit 2.9 and Chrome (Selenium Manager downloads the matching chromedriver). Node.js converts the TRX results into the pipeline reports.
- **Users:** the seeded accounts `user1`-`user20@example.com` and the admin `admin1@lostandfound.com`. Every journey signs in through the real login form.
- **Areas** (case counts add up to 71):

| Area | Cases | What |
|---|---|---|
| AUTH | 10 | Login for user and admin, one generic error for wrong password and unknown e-mail, empty form, route guards (anonymous, user on admin pages, admin on user pages), sign-out then Back, register validation, duplicate e-mail |
| BRW | 7 | Keyword search, empty state, category and type filters and chips, combined filters, clear filters, open details |
| LOST | 20 | Report-lost wizard: happy path, required fields, Back keeps data, future date, non-image and oversized photo, one-photo cap, hidden information never shown; boundary theories (whitespace only, one over the limit, exactly at the limit) |
| FOUND | 14 | Report-found wizard: happy path, required fields on every step, Back keeps data; the same boundary theories |
| RPT | 6 | My Reports: type labels, edit, edit with empty title, delete then cancel, delete confirmed, another user cannot edit |
| ADM | 6 | User Management list and search, kick with cancel, kick confirmed, un-kick, Match Appeals and Spam Review load |
| FLOW | 5 | Cross-service journeys: A reports and B finds it; A edits and B finds the new title; claim dialog; admin kicks and the user cannot log in; spam burst then Spam Review |
| NEG | 3 | Unknown item id, auth cookie removed on a protected page, unknown route |

- **Not tested, by design:**
  - registration beyond client-side validation and the duplicate-e-mail error, and e-mail verification and password reset (they need an e-mailed code, and the environment has no SMTP catcher);
  - Gemini image analysis and match scoring (switched off in the pipeline; the claim journey checks a defined "preview" state instead of a score);
  - cross-browser and visual testing (Chrome only);
  - load (its own category).

## Files in this folder

| File | Purpose |
|---|---|
| `run.sh` | **The only test entry point**, called by GitHub Actions and locally. It checks that .NET and Node.js exist, runs `dotnet test` on the project in Release with headless Chrome (`E2E_HEADLESS=true` unless you override it) and a TRX logger, then calls the report script, which writes `summary.md` and `details.json` even when tests failed. Extra arguments go to `dotnet test`, for example `--filter "Category=Demo"`. It refuses to run if its report folder already has `status.txt`. Its exit code fails the run when any test fails. |
| `scripts/trx-to-report.mjs` | Reads the TRX and the test sources: takes each case's description from its `[Trait("Description", ...)]`, gives every `[Theory]` row its own name (`Test [field: "title"]`), derives the scope line from the area prefixes of the tests that ran, and writes `summary.md` and `details.json`. |
| `LostFound.E2E.Tests/` | The xUnit project, described below. |
| `.gitignore` | Keeps build output, screenshots and results out of git. |

There is no `seed.sh` for this category. The environment's start-up seeding provides the 20 users and the admin, and each test creates the data it needs through the Item API (`Support/ItemApi.cs`), so `seed-test-data.sh ui-e2e-testing` only prints that no category hook was supplied.

**Inside `LostFound.E2E.Tests/`:**

| Folder | Contents |
|---|---|
| `Support/` | `BrowserFixture` (one Chrome per test class, readiness check and warm-up), `E2ETestBase` (per-test setup, screenshot on failure), `TestConfig` (settings, users, reserved accounts), `WaitExtensions` (explicit waits), `ItemApi` (data arrangement through the API), `TestFiles` (generated upload files) |
| `Pages/` | Page Objects, one per screen or component: login, register, home, item details, report wizards, My Reports, edit item, admin users, spam review, claim dialog |
| `Tests/` | Test classes, one per area |

## Shared scripts this calls

All live in `../shared/` and are not changed by this category.

| Script | Called by | What it does for UI/E2E |
|---|---|---|
| `start-environment.sh start` | GitHub Actions; you, locally | Starts MySQL and Kafka, builds and starts the four services in Development (migrations run, Auth seeds the accounts) and the Vite frontend on 5173, and writes `environment.sh`. |
| `start-environment.sh stop` | GitHub Actions; you, locally | Stops the services and removes the containers. |
| `start-environment.sh validate-report ui-e2e-testing` | `run.sh`'s exit handler | Checks `summary.md` and `details.json` exist and are well formed (a non-empty `cases` array, each case with a name, description and valid status, no failed case with exit code 0). |
| `seed-test-data.sh ui-e2e-testing` | GitHub Actions; you, locally | Runs a category `seed.sh` if there is one; there is none here (see above). |

## How a run fits together

```
GitHub Actions (ui-e2e-testing job)            Your machine (WSL2)
  start-environment.sh start                     start-environment.sh start
  seed-test-data.sh ui-e2e-testing               seed-test-data.sh ui-e2e-testing
  ui-e2e-testing/run.sh                          ui-e2e-testing/run.sh  (fresh ASE_REPORT_ROOT)
  start-environment.sh stop                      start-environment.sh stop

ui-e2e-testing/run.sh
  ├─ dotnet test LostFound.E2E.Tests -c Release   (headless Chrome, TRX logger, screenshots on failure)
  │     → results/ui-e2e.trx, screenshots/
  ├─ node scripts/trx-to-report.mjs               → summary.md, details.json
  └─ exit handler → validate-report → status.txt (passed / failed)
```

## Why `run.sh` does not start the environment

The workflow starts the shared environment, runs `run.sh` and stops the environment as separate steps, and a second start would collide on the fixed ports. Start-up takes about 90 s and the full suite about 3 minutes, so starting once lets you run the Demo subset as often as you like. For a demonstration, start the environment well before your slot and do one warm-up run, because Vite compiles pages on the first request.

## Run locally

Set up the machine and the shared environment as in [`../README.md`](../README.md) (WSL2 with Docker Desktop on Windows). From the repository root:

```bash
export ASE_RUNTIME_DIR="$(mktemp -d -t ase-environment.XXXXXX)"
export ASE_COMPOSE_PROJECT=ase-testing-local
bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
bash testing-pipeline/shared/seed-test-data.sh ui-e2e-testing

export ASE_REPORT_ROOT="$(mktemp -d -t ase-reports.XXXXXX)"   # fresh for every run
bash testing-pipeline/ui-e2e-testing/run.sh                    # full suite, about 3 minutes
cat "$ASE_REPORT_ROOT/ui-e2e-testing/summary.md"

bash testing-pipeline/shared/start-environment.sh stop
```

**Demo subset with a visible browser:**

```bash
export ASE_REPORT_ROOT="$(mktemp -d -t ase-reports.XXXXXX)"
E2E_HEADLESS=false bash testing-pipeline/ui-e2e-testing/run.sh --filter "Category=Demo"
```

**Just the tests**, once the environment is running, from `ui-e2e-testing/LostFound.E2E.Tests` (works in PowerShell on Windows too; no report files are produced):

```powershell
dotnet test                              # visible browser, all tests
dotnet test --filter "Category=Smoke"
dotnet test --filter "Category=Demo"
dotnet test --filter "FullyQualifiedName~ReportLostItemWizardTests"
$env:E2E_HEADLESS = "true"; dotnet test  # headless
```

Only one environment can run at a time on a machine, because the ports are fixed. If load tests ran locally first, `stop` that environment before starting a fresh one.

## Run in GitHub Actions

Actions → **ASE testing pipeline** → Run workflow → `ui-e2e-testing` (or `all`) and mode `strict`. The job runs on its own runner with its own fresh environment, in parallel with mutation and load. The artifact contains `summary.md`, `details.json`, the TRX, any failure screenshots and the application logs.

## Test groups

Every test has a `Category` trait and a `Description` trait; the description is the "What it checks" text in the reports.

| Category | Tests | Purpose |
|---|---|---|
| Smoke | 8 | Fast sanity check: AUTH_01, AUTH_02, AUTH_03, AUTH_05, AUTH_06, BRW_01, LOST_01, LOST_08 |
| Demo | 13 | The live demonstration, about 33 s of test time in CI plus browser start-up: AUTH_01, AUTH_03, AUTH_04, AUTH_06, AUTH_08, BRW_01, LOST_01, LOST_05, LOST_07, RPT_02, RPT_05, FLOW_01, FLOW_05 (login, an error path, form validation, access control, sign-out and Back, search, a form wizard, the date and photo rules, edit, delete and two cross-service journeys) |
| Full | the regression tests | Everything; running without a filter executes all 71 cases |

## Service coverage

Tests are organised by user-facing area (AUTH, BRW, LOST, ...), not by backend service, because one browser journey usually crosses several services. The table lists which services each test exercises, judged from what the test does. There is no trait for this, so it cannot be used as a `--filter`; use the area prefix instead, for example `--filter "FullyQualifiedName~FlowTests"`.

Signing in goes through Auth for every test, so Auth is listed only for tests that are about Auth itself (login, route guards, user management, kick, session cookie). Other tests that are about the React app alone are listed under Frontend only.

| Service | Tests |
|---|---|
| Auth | AUTH_01-10, ADM_01-04, ADM_06, FLOW_01, FLOW_02, FLOW_03, FLOW_05, NEG_02 |
| Item | BRW_01-07, LOST_01-12, FOUND_01-06, RPT_01-06, FLOW_01, FLOW_02, FLOW_03, FLOW_06, NEG_01 |
| Admin Verify | ADM_04, ADM_05, ADM_06, FLOW_05, FLOW_06 |
| Matching | FLOW_03, ADM_05 |
| Frontend only | NEG_03 |

Matching is covered least: image analysis (Gemini) is off in the pipeline, so a real match score cannot be produced. The tests cover what does not need one (the claim dialog's candidates and preview state, the Match Appeals page).

## Test data and isolation

- Each test creates the data it needs, with a unique run-id in the title (`ItemApi.UniqueTitle`), and never relies on another test's leftovers. Data is arranged through the API; the behaviour under test is checked in the browser.
- `TestConfig.PickUser()` returns a random user from user2 to user18, so repeated runs spread reports across users. Admin Verify flags a user who has 3 or more listings within 60 minutes (`SpamThreshold` 3, `SpamWindowMinutes` 60). A random user can occasionally reach that, which only creates a spam record that no other test depends on.
- user19 is reserved for the spam burst (FLOW_06) and user20 for kick and un-kick (ADM_04, ADM_06, FLOW_05); `PickUser()` never returns them and the kicked user is restored afterwards.
- Asynchronous effects (Kafka-driven search results, the spam worker) are handled by polling for the visible outcome with explicit `WebDriverWait`s. No UI step sleeps: every wait for the page is an explicit `WebDriverWait`. The only fixed pauses are the HTTP readiness poll in `BrowserFixture`'s start-up check and the short retry back-off in `ItemApi`, which re-sends a data-arrangement API call after a connection error.
- The boundary theories submit no finished report, so they create no data.

## Settings (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `E2E_BASE_URL` | `http://localhost:5173` | Frontend |
| `E2E_AUTH_API_URL` / `E2E_ITEM_API_URL` | `http://localhost:5261` / `http://localhost:5001` | APIs used to arrange data |
| `E2E_HEADLESS` | `false` for `dotnet test`, `true` in `run.sh` | Headless Chrome |
| `E2E_SCREENSHOT_DIR` | next to the test assembly | Failure screenshots (`run.sh` sends them to the report folder) |
| `E2E_USER1_EMAIL` / `E2E_USER1_PASSWORD` | `user1@example.com` / `User123!` | Viewer user |
| `E2E_USERS_PASSWORD` | `User123!` | Password of user2 to user20 |
| `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD` | `admin1@lostandfound.com` / `Admin123!` | Admin account |
| `E2E_KICK_EMAIL` / `E2E_SPAM_EMAIL` | `user20@example.com` / `user19@example.com` | Reserved accounts |
| `E2E_SPAM_DETECTION` | `true` | Set `false` where the spam worker is off; FLOW_06 then skips itself |

## Reports

Written to `$ASE_REPORT_ROOT/ui-e2e-testing/`:

| File | Contents |
|---|---|
| `summary.md` | Counts, scope, out-of-scope, one row per case with status, time and what it checks, and the wall-clock time |
| `details.json` | One case per test (per row for theories): name, description, status, duration |
| `results/ui-e2e.trx` | The native xUnit/VSTest results |
| `screenshots/` | One screenshot per failed test |
| `status.txt`, `console.log` | The runner's status and full console output |

## Assessment-branch note

Auth's login rate limit is raised from 10 to 1000 per 5 minutes on `ASE/Assignment` only. The suite signs in dozens of times per run, so on any other branch it will hit the limit. Registration is limited to 5 per minute, which is why only one test submits the register form.

## Observations made while writing the tests

These are recorded by the tests, not hidden:

- The app has no catch-all route, so an unknown URL shows no page content (NEG_03 records what is shown and checks there is no crash).
- A non-image file chosen in the photo dropzone is silently ignored; there is no rejection message (LOST_06).
- On the found wizard some required-field messages only appear after the field has been visited (FOUND_02 records which ones).
- In the UI source the date picker's maximum is today's UTC date while the validation schema compares with local time, so near midnight the two can disagree.
- After sign-out the app replaces history entries, so Back leaves the app instead of showing the protected page (AUTH_08).
- FLOW_06 used to depend on test order. Admin Verify's Kafka consumer joins its group about a second after the first topic it subscribes to exists, and the Item service only creates a topic when the first listing of that type is published. When a found-item test published first, the consumer joined without `items.lost_item.created`, did not pick it up for the rest of the run, and the spam burst was never seen (one CI run timed out this way; the Kafka and Admin Verify logs showed it). `shared/start-environment.sh` now creates all topics before the services start, so every consumer sees them when it subscribes and the dependency is gone. The application itself still has the weakness: a consumer does not notice a topic created after it joined until its next metadata refresh.
