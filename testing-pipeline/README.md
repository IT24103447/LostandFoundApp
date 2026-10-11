# ASE Testing Pipeline

Automated unit/mocking, mutation, load and UI/E2E testing for the Lost & Found application. Assessment tests live in this folder, separately from the existing CSP tests.

## Contributions and setup

All category runners require Bash and Node.js 22.14.0 for the current reporting framework.

| Category / folder | Tools and initial configuration | Name | Student ID |
|---|---|---|---|
| Unit / `unit-testing` | .NET 8 SDK, xUnit and Moq; reference the service projects | — | — |
| Mutation / `mutation-testing` | .NET 8 SDK and Stryker.NET; configure source scope, unit suites and score threshold | — | — |
| Load / `load-testing` | Java 17+; runner downloads JMeter 5.6.3; configure plans, users and performance thresholds | — | — |
| UI/E2E / `ui-e2e-testing` | .NET 8 SDK, Selenium and Chrome/driver; configure journeys, service URLs and test data | — | — |

Each category's `run.sh` contains its testing commands. Set `ASE_SUITE_READY=true` after implementing and verifying the suite and its reports. Unit, mutation and UI currently remain unfinished. The load runner is enabled, but its current plan exercises Auth only.

## Workflow

GitHub Actions runs on relevant pushes and pull requests targeting `develop`/`main`. Pushes are not restricted to those branches. Manual runs allow selecting a category and development/strict mode.

1. Check readiness and run unit testing.
2. Run mutation, load and UI jobs after the unit job finishes.
3. Mutation requires an executed, passing unit baseline.
4. Load/UI start separate MySQL, Kafka, services and frontend environments.
5. Run category commands, clean up environments and upload results.
6. Display a combined category-status summary.

**Development** permits clearly unfinished categories; **strict** requires all selected categories to execute and pass. Real test, reporting or setup failures fail either mode. Automatic runs default to development unless the repository Actions variable `ASE_PIPELINE_MODE` is set to `strict`. Manual runs use the selected mode.

## Generated files

| File | Contents |
|---|---|
| `status.txt` | Category outcome |
| `console.log` | Test execution output and errors |
| `summary.md` | Scope, counts, failures and measurements |
| `details.json` | Individual case descriptions and outcomes |

Testing commands generate `summary.md` and `details.json`. Runners generate `status.txt` and capture `console.log`. Native tool reports are also uploaded when configured.

Load testing additionally generates `results.jtl`, `jmeter.log` and `html/index.html`. UI/load jobs retain available application logs. Download results from the GitHub Actions run's **Artifacts** section. Reports are temporary output and should not be committed.

## Run locally

Run commands from the repository root. For the shared environment on Windows, use WSL2 with Docker Desktop integration. Install .NET SDK 8.0.424, Node.js 22.14.0/npm and curl inside WSL. Linux uses the same commands. Load testing additionally needs Java 17+.

```bash
export ASE_RUNTIME_DIR="$(mktemp -d -t ase-environment.XXXXXX)"
export ASE_REPORT_ROOT="$(mktemp -d -t ase-reports.XXXXXX)"
export ASE_COMPOSE_PROJECT=ase-testing-local

bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
```

This starts all four services and the frontend:

| Component | URL |
|---|---|
| Auth | http://localhost:5261 |
| Item | http://localhost:5001 |
| Matching | http://localhost:5179 |
| Admin Verify | http://localhost:5003 |
| Frontend | http://localhost:5173 |

Run the required category:

```bash
bash testing-pipeline/unit-testing/run.sh
# Mutation: run only after unit tests pass.
bash testing-pipeline/mutation-testing/run.sh

bash testing-pipeline/shared/seed-test-data.sh load-testing
bash testing-pipeline/load-testing/run.sh

bash testing-pipeline/shared/seed-test-data.sh ui-e2e-testing
bash testing-pipeline/ui-e2e-testing/run.sh
```

Unit/mutation do not need the running application when dependencies are isolated. Windows can run unit projects directly with `dotnet test` once those projects exist. Local unfinished runners return exit 78; development mode only changes CI policy.

Use a fresh `ASE_REPORT_ROOT` for another execution. Stop the environment after testing, including after failures:

```bash
bash testing-pipeline/shared/start-environment.sh stop
```

Email-dependent scenarios additionally need a test SMTP provider. Detailed setup, reporting rules and implementation notes are in the [DevOps documentation](../DevOps_Documentation/ASE-Testing-Pipeline.md). Load-specific instructions are in the [load-testing README](load-testing/README.md).
