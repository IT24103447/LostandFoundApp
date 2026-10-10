# ASE testing pipeline implementation

Date: 10 October 2026 (Asia/Colombo). Scope: shared framework for the SE3112 Microservice Testing Pipeline assessment, using the existing SE3022 application code.

## Files added

Exactly ten source/documentation files were added:

| File | Responsibility |
|---|---|
| `.github/workflows/ase-testing-pipeline.yml` | Automatic/manual orchestration, independent category jobs, artifacts, combined status |
| `testing-pipeline/README.md` | General setup/run instructions, folder/contribution table, executor/report contract |
| `testing-pipeline/shared/docker-compose.yml` | Disposable MySQL/Kafka infrastructure |
| `testing-pipeline/shared/start-environment.sh` | Host application lifecycle, readiness, cleanup, generic runner/report validation |
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
3. Mutation is blocked unless the unit job passed on the same revision. It uses the assessment unit suite; its future executor selects the mutation scope and score threshold.
4. UI/load each receive a separate hosted runner, Compose project, runtime, and report directory. Both can proceed after unit failure. Each starts infrastructure/applications only once its executor exists.
5. Optional category seeding runs after application startup. The executor then runs actual tests and emits the common reports plus native tool reports.
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

All four `run.sh` files delegate to the same runner in `start-environment.sh`. Future category implementations provide `execute.sh` inside their assessment folder. The runner checks for the executor before claiming that any tests ran, captures console output, and requires a human summary and structured detailed evidence for a successful exit.

Common report fields are described in the README. The validator requires meaningful case names/descriptions and recognized statuses. A nonempty suite containing a failed case cannot return success. Native reports preserve debugging evidence and tool-specific measurements. Category executors remain responsible for accurate counts, interpreting tool outcomes, enforcing thresholds, and producing descriptions from their test/scenario metadata. The shared framework cannot verify the truth of fabricated reports or determine appropriate performance/mutation targets.

Artifacts are retained for 14 days. Only reports and application logs are uploaded; the generated environment file is excluded. Executors must keep SMTP credentials, JWTs, user records, and other sensitive runtime data out of reports/logs. Tool-specific detailed results can be accessed through artifacts, with the GitHub summary providing one unified overview.

## Assessment mapping

| Assignment requirement | Framework support | Remaining work |
|---|---|---|
| Use the SE3022 microservice codebase | References/runs existing service and frontend code | New assessment tests must reference these application projects |
| Integrate exactly four selected areas | Unit, mutation, load, UI/E2E jobs and entry points | Supply all four real executors/suites |
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

The framework intentionally starts incomplete: no `execute.sh` files exist and category runners report `not-implemented` with nonzero exit status. The initial workflow should therefore be unsuccessful rather than claim four passing suites. Mutation is blocked by an incomplete unit baseline.

Initial verification covers shell syntax, Compose configuration where Docker tooling is available, the unfinished-category exit/report behavior, and source-file scope. Actual service startup, browser/load/mutation execution, and a hosted GitHub Actions run must be verified when runtime prerequisites and the assessment implementations are available. A passing scaffold check does not demonstrate successful application or assessment tests.

The source package and this document support the pipeline deliverable, but they do not complete the individual testing contributions, presentation, live demonstration, or VIVA requirements.
