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

The four database names are `auth_service`, `item_service`, `matching_service`, and `admin_verify_service`. These names align with the existing migration conventions. Connections use disposable credentials defined in the Compose file and startup script. Local MySQL/Kafka installations and Azure databases are not used. All database data lives in the temporary container; there are no persistent database volumes.

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
