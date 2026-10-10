# ASE testing pipeline implementation

Date: 10 October 2026 (Asia/Colombo). Scope: shared framework for the SE3112 Microservice Testing Pipeline assessment, using the existing SE3022 application code.

## Files added

Exactly ten source/documentation files were added:

| File | Responsibility |
|---|---|
| `.github/workflows/ase-testing-pipeline.yml` | Automatic/manual orchestration, independent category jobs, artifacts, combined status |
| `testing-pipeline/README.md` | General setup/run instructions, folder/contribution table, runner/report contract |
| `testing-pipeline/shared/docker-compose.yml` | Disposable MySQL/Kafka infrastructure |
| `testing-pipeline/shared/start-environment.sh` | Host application lifecycle, readiness, cleanup, shared report validation |
| `testing-pipeline/shared/seed-test-data.sh` | Database bootstrap and optional category data hooks |
| `testing-pipeline/unit-testing/run.sh` | Unit category entry point |
| `testing-pipeline/mutation-testing/run.sh` | Mutation category entry point |
| `testing-pipeline/load-testing/run.sh` | Load category entry point |
| `testing-pipeline/ui-e2e-testing/run.sh` | UI/E2E category entry point |
| `DevOps_Documentation/ASE-Testing-Pipeline.md` | This implementation record |

No earlier CSP tests, application code, deployment workflows, or `.gitignore` were changed. No category-specific test projects, Stryker configuration, Selenium fixtures, or JMeter plans were added.

## Workflow execution

Relevant pushes select all categories. Relevant pull requests targeting `develop`/`main` select all categories. Manual execution supports `all` or one category; mutation additionally selects unit testing. Changes to application code, assessment code, `global.json`, or this workflow trigger automation. Documentation-only changes outside those paths do not.

1. The unit job checks out the repository, selects the pinned SDK and Node runtime, and calls the assessment unit entry point.
2. A separate category matrix coordinates mutation, load, and UI/E2E. `fail-fast: false` preserves other category runs after one fails.
3. Mutation is blocked unless the unit job passed on the same revision. It uses the assessment unit suite; its category's `run.sh` selects the mutation scope and score threshold when implemented.
4. UI/load each receive a separate hosted runner, Compose project, runtime, and report directory. Both can proceed after unit failure. Each starts infrastructure/applications only once `run.sh --check-ready` returns zero. Unfinished categories write incomplete reports without starting infrastructure.
5. Optional category seeding runs after application startup. The category's `run.sh` then runs actual tests and emits the common reports plus native tool reports.
6. Cleanup stops the owned processes and Compose containers and saves infrastructure logs. Artifact upload runs after ordinary failures.
7. The summary job downloads the artifacts and shows every selected category's status. Missing, blocked, unfinished, failed, cancelled, or otherwise non-passed results fail the overall assessment result.

Unselected matrix entries do not execute tests or upload artifacts. They are explicitly shown as not selected in the summary. Branch concurrency cancels superseded runs; cancellation can prevent final reports, unlike ordinary assertion failure. Timeouts bound unit execution at 20 minutes, other-category jobs at 45 minutes, and summary at 5 minutes.

## Shared environment

The current Compose file includes MySQL 8.0.40 and Kafka 7.5.0 with health checks and loopback-only host port mappings. Kafka advertises an internal listener for container tools and an external listener for host application clients. MySQL is available on host port 3307; Kafka is on 19092. No persistent volumes or fixed container names are introduced.

Applications run on the host using the repository's .NET SDK. Startup builds each service in Release and launches its DLL with Development configuration; this permits the existing embedded migrations to run. Auth/Item readiness checks use their Development Swagger documents because they do not expose the health routes used by Matching/Admin. HTTP readiness confirms startup, not the correctness of Kafka processing or an end-to-end business transaction; the assessment tests must verify those behaviors.

Service ports are Auth 5261, Item 5001, Matching 5179, and Admin Verify 5003. Frontend uses Node 22.14.0, installs from the existing lockfile, and launches Vite on 5173. MySQL database names match the service migration conventions. Development Auth startup supplies its existing development accounts; the new seed script only creates databases and invokes optional assessment `seed.sh` hooks.

All services receive the same disposable JWT key, issuer/audience, local Kafka, CORS, and test database settings. Cloud telemetry/blob configuration is cleared. Photo uploads use runtime storage. Gemini processing and Matching/Admin notifications are off. Real SMTP, external AI, and notification delivery remain explicit scenario prerequisites; no coverage of those paths is implied by environment startup. Auth SMTP requires TLS, so an unauthenticated plaintext mail catcher would not work without an application change. No application change or SMTP infrastructure was included in this scope.

Cleanup records PIDs of directly launched DLL/Vite processes, avoiding npm/dotnet-run wrapper processes. Container teardown targets only the configured assessment Compose project. Runtime logs survive cleanup; database containers do not. Local concurrent environments require different host ports, which are not parameterized in this initial implementation.

## Runner and reporting protocol

Each category's existing `run.sh` directly contains its testing commands in a `run_tests()` function. The shared environment script no longer delegates to another testing script. Before implementing a suite, `ASE_SUITE_READY=false` keeps its status visibly incomplete. After replacing the placeholder with real commands and reporting, set that flag true. The no-side-effect `--check-ready` option lets CI avoid starting infrastructure for unfinished categories.

Each runner captures console output and uses an exit handler to record failure, incomplete, or passed status. A successful command must also pass the shared `validate-report` check before the runner can record `passed`. The environment script retains that reporting helper, along with `start` and `stop`, but does not launch tests. This correction changes nine existing framework files (including the seeding message) and adds no files or testing suites. The earlier invalid job-level runner expressions are also absent; temporary paths are initialized inside job steps using `RUNNER_TEMP` and `GITHUB_ENV`.

Common report fields are described in the README. The validator requires meaningful case names/descriptions and recognized statuses. A nonempty suite containing a failed case cannot return success. Native reports preserve debugging evidence and tool-specific measurements. Category testing commands remain responsible for accurate counts, interpreting tool outcomes, enforcing thresholds, and producing descriptions from their test/scenario metadata. The shared framework cannot verify the truth of fabricated reports or determine appropriate performance/mutation targets.

Artifacts are retained for 14 days. Only reports and application logs are uploaded; the generated environment file is excluded. Testing commands must keep SMTP credentials, JWTs, user records, and other sensitive runtime data out of reports/logs. Tool-specific detailed results can be accessed through artifacts, with the GitHub summary providing one unified overview.

## How to implement each category's run.sh

### Why shell scripts are used

A `.sh` file is a shell script: a sequence of commands executed by Bash. The existing CSP workflows put commands such as `dotnet test services/AuthService.Tests` directly into YAML `run:` steps. GitHub executes those commands using a shell, so those workflows do not need a separate shell script.

The assessment uses one `run.sh` per category so the same command runs locally and in GitHub Actions. Each implementation can be updated in its own category folder without changing the shared orchestration. The assignment requires systematic automation; it does not require shell scripts or particular filenames. No additional test-entry script is needed.

The execution path is: GitHub Actions or a local terminal calls the category's `run.sh`, which calls its own `run_tests()` function and the selected testing tool. The shared reporting helper validates results but does not execute the tests.

### Changes required in the existing runner

1. Add the real assessment tests and category configuration under the appropriate `testing-pipeline/` folder. Reference application code in `services/` or run the shared application environment; do not execute the earlier CSP suites.
2. Replace the placeholder body of `run_tests()` in that category's existing `run.sh`. Put tool setup, actual test commands, scenario setup if needed, report generation, and threshold evaluation in that function.
3. Write `summary.md`, `details.json`, and native tool reports into the provided `ASE_REPORT_DIR`. Keep descriptions and outcomes tied to actual test results.
4. Propagate failures as nonzero exits. If reports must be generated after a tool fails, capture its exit status, generate the available reports, and then return that original failure. Do not turn a failed test into success merely to keep reporting running.
5. Set `ASE_SUITE_READY=true` only after the suite and reporting work. Changing the flag without replacing the placeholder still returns exit code 78.
6. Run the category locally, check its reports, and measure the complete execution time. Then verify the same category in GitHub Actions.

Keep the existing path setup, `--check-ready` handling, logging, report validation, and exit handler. The readiness option returns zero for a ready suite or 78 for an unfinished suite without running tests, installing tools, creating records, or writing reports. CI uses it to avoid unnecessary environment startup. `run_tests()` starts from the repository root and receives extra command-line arguments; no demonstration argument is implemented yet.

| Category | Commands and configuration to implement inside run_tests() |
|---|---|
| Unit testing | Run the new assessment unit projects for the service code, use parameterized cases and mocks/stubs, generate TRX results and optional coverage, and produce individual case descriptions/outcomes. |
| Mutation testing | Install/restore the chosen Stryker.NET version, select source scope and the assessment unit suites, run mutation analysis, enforce the agreed score threshold, and generate mutant details and HTML/JSON reports. |
| Load testing | Install the chosen JMeter/Java versions, prepare fresh scenario data, run bounded command-line load plans, evaluate error-rate/response-time thresholds, and generate JTL results and the HTML dashboard. |
| UI/E2E testing | Install browser/Selenium dependencies, prepare fresh scenario data, execute browser journeys, and generate test results, descriptive case details, and failure screenshots. |

CI starts the shared application environment before implemented UI/load runners. Those runners should not start it again. For local execution, start/source the shared environment first as described in the README. UI/load setup can use the optional `seed.sh` hook or prepare records inside `run_tests()`; the hook is optional, not another required test entry point.

### Files produced by a run

| Output | Contents | Generated by |
|---|---|---|
| `status.txt` | Overall category status: running, passed, failed, or not-implemented. CI can separately record blocked mutation or setup failure. | Existing runner/CI code. |
| `console.log` | Terminal output, tool messages, errors, and the report validator's detailed case output when validation runs. | Existing runner logging. |
| `summary.md` | Human-readable scope, executed/passed/failed/skipped counts, failures, and applicable scores/thresholds. | Category testing/report commands; the runner supplies a basic fallback after failure. |
| `details.json` | A nonempty `cases` array for an implemented successful suite, with a name, purpose/description, and outcome for each case, scenario, or mutant; timing and error fields can also be included. | Category testing/report commands. |
| Native tool reports | Unit TRX/optional coverage; Stryker HTML/JSON; JMeter JTL/HTML; UI test results and failure screenshots where configured. | Selected testing tools and category commands. |

Detailed descriptions must explain the behavior checked, not only repeat a test name. Mutation evidence identifies the source location and change made and whether it was killed, survived, or had another outcome. Load evidence identifies scenarios/endpoints and reports concurrency, request counts, error rate, response-time percentiles, and threshold results. Summaries should clearly distinguish an incomplete suite from a failed assertion.

The current stubs produce a not-implemented status, explanatory summary/console output, and an empty cases array. These are incomplete-state records, not proof of executed tests. Once implemented, actual results replace those placeholders. On failures, available reports are retained; a crash before report creation can leave some reports absent.

Reports are saved under `ASE_REPORT_ROOT/<category>/`, normally in temporary directories. GitHub uploads category reports and available UI/load application/infrastructure logs as `ase-*` artifacts and displays a combined status table in the run summary. Application logs are separate from `console.log`, which records the category runner. The combined summary is a GitHub display, not an additional mandatory file each category must create.

### Test scope and the two-minute demonstration

The assessment assigns two minutes per individual to execute and explain their testing strategy. It does not specify a minimum test count, complete acceptance-criterion coverage, every line of code, or a required coverage percentage. It also does not explicitly require every test in the full suite to finish within that window. These are distinctions in the assessment wording, not a guarantee of marks for a small suite.

The planned approach is a focused, meaningful suite across the services, timed so it can be demonstrated with explanation. Select tests that show the assigned technique and relevant failure/boundary conditions rather than choosing only happy paths to reduce runtime. For unit testing, useful examples include a successful operation, parameterized invalid/boundary input, a handled dependency failure, and verification that a dependency action happens or is prevented. Mutation, load, and UI implementations need equivalent depth in their own techniques. The rubric rewards implementation depth, edge cases, reliability, and understanding; low test count alone does not satisfy the highest band.

Measure the entire demonstration: testing, report generation, opening the report, and explanation. An execution/reporting target of roughly 45–60 seconds leaves time to explain results within two minutes, but this is a planning target and has not been verified. Small text/JSON summaries usually add little overhead; browser startup, coverage processing, mutation analysis, and native HTML dashboard generation can take longer. The framework does not guarantee a two-minute runtime.

If a category's full suite is too slow, a representative demonstration mode can be implemented inside `run_tests()` while preserving the full suite for CI. That option is not currently implemented; CI does not currently pass a demo argument. Any reduced mode must label its tested scope honestly. Mutation analysis may particularly need a smaller source scope for the live execution and a previously generated full report for discussion.

### What to prepare once and what repeats

| Preparation | Local execution | GitHub-hosted CI |
|---|---|---|
| Install .NET, Node, Docker, and testing tools | Usually once per machine; repeat when versions change. | Each fresh job prepares its required runtimes/tools. |
| Download dependencies | Initially and when dependencies change; package caches often reuse downloads. | Restore/install per job; downloads may be cached, but caching is not a persistent running environment. |
| Build the projects | Before the first execution and after relevant code changes. | Build as required in each job. |
| Start databases/services/frontend | Each session after stopping them; unnecessary for isolated unit/mutation suites. | Separate UI/load jobs start their own disposable environment. |
| Create scenario records | As required to keep each test run predictable and independent. | Create fresh records for each disposable test environment. |
| Execute tests and generate reports | Every execution. | Every selected implemented category run. |

For the live VIVA, install tools, prepare builds, and start the required environment before the individual presentation slot. Then demonstrate the actual tests and their outputs. The group introduction can show how automation coordinates setup and testing. A complete fresh CI execution is not promised to finish in two minutes.

The current shared `start` command builds the services and runs `npm ci` every time it starts an environment; no reuse-existing-build mode has been added. Downloaded packages/container images can be reused locally, but startup and scenario preparation are not one-time machine installation tasks. Stopping the disposable environment removes its database containers; a later start creates a new environment. Keep that distinction clear when timing or rehearsing demonstrations.

## Assessment mapping

| Assignment requirement | Framework support | Remaining work |
|---|---|---|
| Use the SE3022 microservice codebase | References/runs existing service and frontend code | New assessment tests must reference these application projects |
| Integrate exactly four selected areas | Unit, mutation, load, UI/E2E jobs and entry points | Implement the suites and testing commands in the four run.sh files |
| Systematic test automation | Automatic GitHub Actions triggers and manual category selection | Validate complete runs after suites exist |
| Highest automation band: CI/CD and unified reporting | Failures propagate, common statuses, combined summary, downloadable artifacts | Verify native reports and end-to-end CI reliability |
| Unit: parameterized cases and mocks/stubs | Dedicated assessment unit entry point | Implement these test techniques and isolated service logic |
| Mutation: evaluate unit-suite quality | Passing-unit dependency | Install/configure Stryker, scopes, score thresholds, analysis |
| Load: stress/high concurrency on key endpoints | Dedicated isolated environment and data hook | Implement profiles, assertions, metrics, meaningful thresholds |
| UI/E2E: automated frontend or service workflows | Real local services/frontend and category entry point | Install browser tools, implement journeys and scenario data |
| Live group/individual demonstrations | Manual full/category runs and local commands | Rehearse against completed suites; prepare fast representative demos |
| README with setup, commands, contributions | General README and blank name/ID contribution table | Populate real contribution fields and category setup details |
| Slides (max 5) or one-page architecture PDF | Workflow explanation available as source material | Create presentation/architecture deliverable separately |

The assessment does not mandate exact push filters, Docker, a report schema, or score thresholds. These are implementation choices to support reproducibility and the rubric. Code Quality & Metrics is not selected as an assessment area; the rubric's individual Code Quality & Standards criterion still applies to implementation readability and tool use.

## Current limits and validation

The framework intentionally starts incomplete: all four `run.sh` files contain placeholders with `ASE_SUITE_READY=false` and report `not-implemented` with nonzero exit status. No additional test-entry script is required. The initial workflow should therefore be unsuccessful rather than claim four passing suites. Mutation is blocked by an incomplete unit baseline.

Initial verification covers shell syntax, Compose configuration where Docker tooling is available, the unfinished-category exit/report behavior, and source-file scope. Actual service startup, browser/load/mutation execution, and a hosted GitHub Actions run must be verified when runtime prerequisites and the assessment implementations are available. A passing scaffold check does not demonstrate successful application or assessment tests.

The direct-runner correction was verified with syntax checks for all six shell files and seven inline workflow scripts, Compose configuration validation, and temporary fixtures. All four readiness checks returned 78 without creating reports or starting infrastructure; normal unfinished runs produced incomplete reports. Fixture checks confirmed detailed output for valid reports, propagation of command failure, rejection of missing/empty/failed evidence, and continued incomplete status when only the readiness flag is changed. No fixture files or fake results were added to the repository.

The source package and this document support the pipeline deliverable, but they do not complete the individual testing contributions, presentation, live demonstration, or VIVA requirements.
