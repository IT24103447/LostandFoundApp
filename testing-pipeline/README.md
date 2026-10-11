# ASE assessment testing pipeline

This folder contains the separate SE3112 assessment testing framework for the existing .NET 8 microservices and React frontend. Application code remains in `services/` and `frontend/`. This workflow does not execute the earlier CSP suites in `services/*.Tests/`, `tests/e2e/`, or `tests/performance/`.

**Current state:** shared infrastructure, category entry points, and CI coordination are implemented. Assessment test suites and testing commands are not implemented yet. Each category's `run.sh` is its only test entry point. An unfinished category returns exit code 78 and is reported as `not-implemented`; the complete pipeline cannot pass until the selected categories contain real tests and reporting.

## Folder map and contribution record

| Folder | Purpose / testing category | Name | Student ID |
|---|---|---|---|
| `shared/` | Common environment setup, database bootstrap, runner/report protocol | | |
| `unit-testing/` | Unit testing and mocking of the four service projects | | |
| `mutation-testing/` | Mutation analysis using the assessment unit-test suite | | |
| `load-testing/` | Load and performance tests against application endpoints | | |
| `ui-e2e-testing/` | Selenium UI tests and end-to-end application workflows | | |

Fill the contribution columns before submission. No student names or IDs have been assumed.

## Prerequisites

- Bash on Linux or WSL2. On Windows, run the project and its tools consistently inside WSL2 for environment startup. Git Bash can run the basic category stubs, but the full lifecycle is designed for Linux/WSL2.
- .NET SDK selected by the repository's `global.json` (8.0.424).
- Node.js 22.14.0 and npm, used by the frontend and report validator.
- Docker with Compose v2 supporting `up --wait`, with the Docker daemon running, for UI/load environments.
- `curl` and standard Bash utilities.
- Additional category tools installed inside the category's `run.sh`: Stryker.NET, JMeter/Java, and Selenium/browser dependencies as required. Their installation is not part of this initial framework.

Run all commands below from the repository root. Scripts are invoked with `bash`, so executable Git file permissions are not required.

### Windows: WSL2 setup (once per machine)

The scripts target Linux, the same as the GitHub runner. On Windows, run them inside a WSL2 distribution (tested with Ubuntu 22.04) from an interactive WSL terminal. The Windows checkout is reachable at `/mnt/c/...`.

1. Docker Desktop with **WSL integration** enabled for the distribution. Check with `docker info`.
2. .NET SDK 8.0.424 inside WSL. A Windows SDK or Node found through the Windows PATH does not work.
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o ~/dotnet-install.sh
   bash ~/dotnet-install.sh --version 8.0.424 --install-dir ~/.dotnet
   echo 'export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH' >> ~/.bashrc
   ```
3. Node 22.14.0 inside WSL (via nvm), so the Linux `node`/`npm` take precedence over the Windows ones.
   ```bash
   curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash
   source ~/.bashrc
   nvm install 22.14.0
   nvm alias default 22.14.0
   ```
4. Open a new WSL terminal. Then check `dotnet --version` (from the repo root, it should print `8.0.424`), `node --version` (`v22.14.0`), `docker compose version` and `curl --version`. Category tools (for example Java for JMeter) are listed in that category's section.

These paths load from `~/.bashrc`, which Ubuntu only reads in interactive shells. From outside WSL, use `wsl -e bash -ic "..."`.

**Line endings:** the repository's `.gitattributes` keeps `*.sh` files on LF line endings in every checkout. If a checkout predates that file, refresh the scripts once with `git rm --cached -r -q testing-pipeline` and then `git checkout HEAD -- testing-pipeline` (see change 1).

## Start the project in the assessment environment

Do not run another copy of the application on the same ports. Infrastructure uses different ports from the existing local MySQL/Kafka defaults, but application ports remain the project's established defaults.

```bash
export ASE_RUNTIME_DIR="$(mktemp -d -t ase-environment.XXXXXX)"
export ASE_REPORT_ROOT="$(mktemp -d -t ase-reports.XXXXXX)"
export ASE_COMPOSE_PROJECT=ase-testing-local
bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
```

Startup waits for MySQL and Kafka, creates the service databases, builds and starts all four services in Development, waits for their HTTP endpoints, installs frontend dependencies, and starts Vite. Each service applies its existing embedded migrations. Auth Service's existing Development startup also creates its development accounts. No new assessment accounts or scenario records are created by this framework.

| Component | Local address |
|---|---|
| Frontend | `http://localhost:5173` |
| Auth Service | `http://localhost:5261` |
| Item Service | `http://localhost:5001` |
| Matching Service | `http://localhost:5179` |
| Admin Verify Service | `http://localhost:5003` |
| MySQL | `localhost:3307` |
| Kafka | `localhost:19092` |

The four database names are `auth_service`, `item_service`, `matching_service`, and `admin_verify_db`. These names align with the existing migration conventions (Admin Verify's migration runner only accepts `admin_verify_db`; see change 3). Connections use disposable credentials defined in the Compose file and startup script. Local MySQL/Kafka installations and Azure databases are not used. All database data lives in the temporary container; there are no persistent database volumes.

On the `ASE/Assignment` branch, Auth Service's login rate limit is raised from 10 to 1000 logins per 5 minutes so load tests can sign in 20 real users on every run. That branch is never merged into `develop`/`main`, so production keeps 10 per 5 minutes (see "Changes after the original implementation", change 4).

The startup script overrides JWT, Kafka, CORS, storage, and service URL settings. Gemini image processing and Matching/Admin email notifications are disabled. Item photos are written under the runtime directory. These defaults support a basic local environment; photo-analysis and notification scenarios require explicit additional configuration and cannot be claimed as covered under these defaults.

### Email workflows

Auth Service always attempts SMTP for verification/reset messages and its implementation requires TLS. The framework does not supply an SMTP server. Email-dependent tests must configure a dedicated TLS-capable test SMTP provider and a way to read its captured messages. Set `ASE_SMTP_HOST`, `ASE_SMTP_PORT`, `ASE_SMTP_USER`, `ASE_SMTP_PASSWORD`, and `ASE_SMTP_FROM` before startup. Defaults point to an unprovided localhost SMTP endpoint; email delivery will not work with defaults. Never use production recipients. For GitHub runs, map the required test-provider secrets to the environment in the workflow when implementing those scenarios; the initial workflow does not map any SMTP secrets.

## Run the assessment categories

Unit and mutation suites should isolate external dependencies and therefore do not require the shared application environment. Mutation analysis must follow a passing unit baseline for the same code revision.

```bash
bash testing-pipeline/unit-testing/run.sh
# Run only after the unit command passes:
bash testing-pipeline/mutation-testing/run.sh
```

For UI/load testing, start and source the shared environment first, then prepare scenario data and run the category:

```bash
bash testing-pipeline/shared/seed-test-data.sh ui-e2e-testing
bash testing-pipeline/ui-e2e-testing/run.sh

bash testing-pipeline/shared/seed-test-data.sh load-testing
bash testing-pipeline/load-testing/run.sh
```

For a local full demonstration, run these categories against separate fresh environments when they change records or generate concurrent load. In CI, UI and load already have separate runners/environments. Use a fresh `ASE_REPORT_ROOT` for each execution; the runner refuses to overwrite a category that already has a recorded status, avoiding stale reports being mistaken for new evidence.

Stop the application and infrastructure when finished:

```bash
bash testing-pipeline/shared/start-environment.sh stop
```

Cleanup stops recorded application processes and removes the dedicated Compose containers. It retains runtime logs/reports for inspection and does not delete the repository or other Compose projects. Use a unique `ASE_COMPOSE_PROJECT`/runtime per environment. Fixed host ports mean two environments cannot run concurrently on the same host.

## Changes after the original implementation (11 October 2026)

The original framework was verified with syntax checks, Compose validation and stub runs. Those never started the shared environment, because every category was still a stub. The first real start, for the load-testing category in WSL2 on Windows, needed the changes below before the environment and its scripts worked. Each change is a separate commit on `ASE/Assignment`.

### 1. Line endings: `.gitattributes` keeps shell scripts on LF

- **Observed:** on a Windows checkout, every `testing-pipeline` script failed in WSL2 bash with errors such as `$'\r': command not found`.
- **Cause:** Git for Windows defaults to `core.autocrlf=true`. The repository stores the scripts with LF line endings, but they were checked out with CRLF. Bash only accepts LF, so every line ended in a stray carriage return. `git ls-files --eol testing-pipeline/load-testing/run.sh` showed `i/lf w/crlf`. The GitHub runner (Ubuntu) checks out LF, so CI was not affected. Local runs from Windows, including live demonstrations, were.
- **Change:** a new `.gitattributes` at the repository root containing `*.sh text eol=lf`. It forces LF for shell scripts on every checkout, whatever the machine's `core.autocrlf` setting. Repository contents are unchanged (already LF), and no other file types are affected. Cleared with DevOps.
- **Existing Windows checkouts** refresh their scripts once: `git rm --cached -r -q testing-pipeline`, then `git checkout HEAD -- testing-pipeline`. Use `git checkout HEAD -- …`: after `git rm --cached`, a plain `git checkout -- …` has nothing to restore from and fails with "pathspec did not match". New clones are correct automatically.
- **Verified:** `git check-attr eol` reports `lf`, and all six `.sh` files show `i/lf w/lf`.

### 2. MySQL health check over TCP

- **Observed:** `start-environment.sh start` intermittently failed right after Compose reported MySQL as healthy, with `ERROR 2013 (HY000) at line 2: Lost connection to MySQL server during query` while creating the databases. The startup trap then removed the containers.
- **Cause:** on first start, the `mysql:8.0.40` image runs a temporary initialisation server with networking disabled (socket only), then shuts it down and starts the real server. The health check ran `mysqladmin ping -h localhost`, which connects through the socket, so it passed against the temporary server. The database bootstrap then connected just as that server shut down.
- **Change:** in `testing-pipeline/shared/docker-compose.yml`, the health check pings `-h 127.0.0.1`, which forces TCP. The temporary server doesn't listen on TCP, so the container only becomes healthy once the real server accepts connections.
- **Verified:** subsequent starts passed the database bootstrap every time.

### 3. Admin Verify database name: `admin_verify_db`

- **Observed:** every start failed with `Application stopped before readiness: http://localhost:5003/health`. Admin Verify's log showed `System.InvalidOperationException: The configured database must be admin_verify_db.` from `DbInitializer.RunPendingMigrations`.
- **Cause:** Admin Verify's migration runner deliberately refuses to migrate any database not named `admin_verify_db`, a guard against migrating the wrong database. The shared scripts created and connected to `admin_verify_service`.
- **Change:** `admin_verify_db` in `testing-pipeline/shared/seed-test-data.sh` (the `CREATE DATABASE` statement) and in `testing-pipeline/shared/start-environment.sh` (the services table), plus the database names listed in the README and the DevOps implementation record. Auth, Item and Matching keep `auth_service`, `item_service` and `matching_service`.
- **Verified:** Admin Verify now starts, applies its migrations and passes `/health` on port 5003.

### 4. Auth login rate limit raised on the assessment branch

- **Observed:** Auth's `"login"` rate limiter is **global**: one fixed window of 10 logins per 5 minutes shared by every caller, active in Development. A load test with 20 users logging in gets `429 Too Many Requests` from the 11th login on. Waiting for the window (up to 5 minutes) does not fit the two-minute demonstration.
- **Change:** in `services/AuthService/Program.cs`, the `"login"` limiter's `PermitLimit` goes from 10 to 1000. The 5-minute window and the other five limiters (register, verify-email, resend-verification, forgot-password, reset-password) are unchanged.
- **Why it is safe:**
  - `ASE/Assignment` only receives merges from `develop` and is never merged back.
  - `auth-service-ci-cd.yml` only runs for `develop`, `main` and `feature/**`, and only deploys from `develop`.
  - So production keeps 10 per 5 minutes. Never open a pull request from `ASE/Assignment` into `develop` or `main`.
- **Impact check:** the login limit is referenced only by that policy and `[EnableRateLimiting("login")]` on the login action. The frontend's 429 handling belongs to other forms, and no test or JMeter plan depends on the login limit's value.
- **Verified:** AuthService builds with 0 warnings; AuthService.Tests passes 145/145.

### 5. Windows / WSL2 local setup

- **Observed:** on Windows, the scripts need Linux tooling. Under Git Bash, the PID-based `stop` cannot terminate the real Windows `dotnet.exe` processes. In a fresh WSL2 Ubuntu, `dotnet` and `node` were missing, and the `npm` found was the Windows one, leaking through the Windows PATH.
- **Change:** documented the one-time WSL2 setup under **Prerequisites → Windows: WSL2 setup** in `testing-pipeline/README.md`:
  - Docker Desktop with WSL integration;
  - .NET SDK 8.0.424 and Node 22.14.0 installed **inside** WSL (dotnet-install script and nvm);
  - using an interactive WSL terminal, because the paths load from `~/.bashrc`.
- **Verified:** inside WSL2 (Ubuntu 22.04), `dotnet --version` from the repository root is `8.0.424`, `node --version` is `v22.14.0`, and Docker Compose v5.1 is reachable.

### Result

With changes 1–5, in WSL2 (Ubuntu 22.04):
- `start-environment.sh start` completed in **88 seconds**: MySQL and Kafka healthy, all four services ready (Auth 5261, Item 5001, Matching 5179, Admin Verify 5003), and the frontend on 5173.
- `seed-test-data.sh load-testing` then ran the load-testing seed: 6 users logged in through the real API and 12 listings were created, all of which search returns.

## Run in GitHub Actions

1. Commit the framework to GitHub. The workflow is `.github/workflows/ase-testing-pipeline.yml`.
2. Relevant pushes and pull requests to `develop`/`main` automatically select all four categories. Push triggers are not restricted to those branches.
3. For a manual run, open **Actions → ASE testing pipeline → Run workflow**, select a branch and category. GitHub exposes this button after the workflow exists on the default branch.
4. `all` selects the complete assessment. Selecting mutation also runs unit testing first. Selecting a single UI/load category does not run the unit suite.
5. Read the final summary, category logs, and downloadable `ase-*` artifacts. Artifacts are retained for 14 days.

Unit testing runs first. Mutation requires unit success. UI and load execute independently and are not blocked by a unit failure. The matrix keeps running other categories after one fails. Before starting an environment, CI calls the category's `run.sh --check-ready`. An unfinished category then runs its normal stub to write `not-implemented` reports, without starting infrastructure. This check does not execute tests or write reports itself.

This workflow only tests; it does not deploy or alter the existing CSP CI/CD workflows. It does not add SonarQube/static analysis as an assessment category. Cancellation supersedes report/cleanup guarantees; GitHub disposes its hosted runners, but a cancelled job may have no artifact. The final summary treats missing selected-category evidence as unsuccessful.

## Connect a testing implementation

Edit the existing `run.sh` inside the appropriate category folder. Replace the `run_tests()` placeholder with tool installation, actual assessment test commands, threshold evaluation, and report generation. Keep failed commands as nonzero exits. The script changes to the repository root before calling `run_tests`, and forwards extra arguments to that function. No additional test-entry script is required.

After the real suite and reporting are implemented, set `ASE_SUITE_READY=true` in that same file. CI calls `run.sh --check-ready` before starting the UI/load environment; it returns 0 for a ready suite or 78 for an unfinished suite. Keep this check free of installation, test execution, and data changes. Setting the flag alone does not make the placeholder pass: it still returns 78. Successful testing commands must also produce valid nonempty reports.

For UI/load categories, optionally add `seed.sh` alongside `run.sh`. The shared seeding script calls this hook after startup with the test environment variables available. Alternatively, create the scenario data directly inside `run_tests()`. Create fresh accounts/records using unique identifiers and write needed IDs/tokens to private runtime data, not publicly downloadable reports. The shared bootstrap does not assert that a category has sufficient data.

Testing commands receive `ASE_CATEGORY`, `ASE_REPORT_DIR`, and `ASE_REPORT_ROOT`; UI/load additionally receive `ASE_MYSQL_CONNECTION` and the `VITE_*` service URLs after sourcing `environment.sh`.

### Required output contract

Each implemented `run_tests()` must write these nonempty files into `ASE_REPORT_DIR`:

- `summary.md`: a brief human-readable result with executed counts, skips, failures, scope, and applicable thresholds.
- `details.json`: structured evidence with a nonempty `cases` array. Each entry has a meaningful `name`, `description` explaining what is verified, and `status`. Optional fields include `durationMs`, `error`, endpoint, source location, and performance metrics.
- Native reports as applicable: TRX/coverage, Stryker HTML/JSON, JMeter JTL/HTML, Selenium screenshots/results. These supplement the shared summary.

Example shape (illustrative; not a real test result):

```json
{
  "cases": [
    {
      "name": "Reject invalid registration email",
      "description": "Checks that invalid email input is rejected before a user is stored.",
      "status": "passed",
      "durationMs": 12
    }
  ]
}
```

Accepted statuses: `passed`, `failed`, `skipped`, `killed`, `survived`, `timeout`, `no-coverage`, `compile-error`. The shared report validator rejects an empty suite, invalid/missing fields, an all-skipped/all-compile-error report, or failed cases paired with exit code zero. Each `run.sh` must enforce its mutation/performance thresholds and interpret other statuses accurately; the generic framework cannot determine those category-specific thresholds. After successful testing commands, `run.sh` calls the validator, which prints each reported case's description/status and available timing/error details. Tool and validator output are preserved in `console.log`. On tool failure, the native console log and category summary remain available; testing commands should still write partial detailed results when possible.

Mutation details should identify each mutant and its outcome rather than call a surviving mutant a passed test. Load details should identify scenarios/endpoints and include concurrency, requests, error rate, response-time percentiles, and threshold outcomes. Test names alone are insufficient to explain purpose; test/scenario metadata must supply the descriptions.

`status.txt` and `console.log` are generated directly by each `run.sh`. Its exit handler records command failures and validates reports before marking a run passed. The shared script only manages the environment and validates reports; it does not launch category tests. The summary job gathers category status without turning unavailable suites into passes. Generated data defaults to OS temporary directories because this change does not update `.gitignore`. Do not commit generated reports, runtime files, credentials, screenshots containing private data, or browser profiles.

## Troubleshooting

- `not-implemented` / exit 78: implement `run_tests()` in the category's existing `run.sh`, generate reports, and then set `ASE_SUITE_READY=true`; no assessment tests have run while the stub remains.
- `blocked` mutation: fix the assessment unit baseline first.
- Setup failure: inspect application build/startup logs in the artifact or `$ASE_RUNTIME_DIR/logs`.
- Port conflict: stop the conflicting application/environment before startup.
- Existing result directory: choose a new `ASE_REPORT_ROOT` for another run.
- SMTP failure: configure a TLS-capable test provider; defaults do not deliver email.
- Missing summary/details: implement the reporting contract; an exit code zero alone is insufficient.

For implementation details and the assessment requirement mapping, see `DevOps_Documentation/ASE-Testing-Pipeline.md`.
