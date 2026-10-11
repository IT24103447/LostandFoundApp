# Load and performance testing

Apache JMeter load tests for the Lost & Found microservices (Auth, Item, Matching, Admin Verify). This folder is the load-testing category of the ASE testing pipeline. Shared setup, the CI workflow and the reporting contract are described in [`../README.md`](../README.md).

> **Status:** environment, data, seed, configuration and the local helper are done. The JMeter plan, the results summary and `run.sh`'s `run_tests()` are in progress. Until `run.sh` is finished it still reports `not-implemented` (exit 78).

## What is tested

- **Users:** 20 seeded users (`user1`–`user20@example.com`) and 1 admin (`admin1@lostandfound.com`). All log in through Auth's real login endpoint and send the returned JWT on every later request.
- **Endpoints** (named exactly like this in the results):

| Service | Requests |
|---|---|
| Auth | `Auth - login`, `Auth - me`, `Auth - admin user list` |
| Item | `Item - search`, `Item - view listing`, `Item - my lost reports`, `Item - my found reports`, `Item - create lost listing` |
| Matching | `Matching - my matches`, `Matching - my claims`, `Matching - my appeals` |
| Admin Verify | `AdminVerify - spam review list` |

- **Load profile** (about 60 seconds, `config/load.properties`):
  - 20 steady users ramp up over 20 s, then hold a 25 s peak.
  - 20 spike users then start within 1 s and run for 15 s with no think time.
  - 1 admin thread runs the whole 60 s.
- **Not tested, by design:**
  - registration, email verification and password reset (they need SMTP, which the environment lacks);
  - photo uploads (storage-bound);
  - claims, decisions, appeals and admin actions (they race on shared records or need prepared match states, and 409s would count as errors);
  - resolve, update and delete.

## Files in this folder

| File | Purpose |
|---|---|
| `run.sh` | **The only test entry point**, called by GitHub Actions and locally. Installs JMeter, runs the plan, checks the thresholds, and writes the reports (see "What `run.sh` does"). *In progress.* |
| `seed.sh` | Scenario data. Run by the shared `seed-test-data.sh load-testing` after the environment starts. Logs in the first 6 users through the real API and creates 1 lost plus 1 found listing each (12 total, unique `(seed <runId>)` titles, no photos), so search and view have data from the first request. Two per user stays below Admin Verify's spam rule (3 in 60 minutes). |
| `local-run.sh` | **Local convenience only; CI does not use it.** Wraps the documented local commands (see "Run locally"). |
| `config/load.properties` | The load profile: users, ramp, peak and spike lengths, think times. Each value can be overridden with `-J<name>=<value>`. |
| `config/thresholds.json` | Pass/fail limits checked after the run: error rate ≤ 1%; p95 per thread group (steady 1000 ms, with login and create at 2000 ms; spike 3000 ms; admin 1000 ms, with login at 2000 ms). Starting values, to be calibrated. |
| `data/users.csv` | `user1`–`user20` login details, one row per user thread (Development seed accounts, test-only passwords) |
| `data/admin.csv` | `admin1` login details |
| `data/search-terms.csv` | Search word, category and type combinations, so searches vary instead of repeating one cached query |
| `plans/lost-and-found-load.jmx` | The JMeter plan, organised as one Test Fragment per service called from the user and admin journeys. *In progress.* |
| `scripts/summarize-results.mjs` | Reads `results.jtl`, works out per-endpoint and per-group statistics, checks `config/thresholds.json`, and writes `summary.md` and `details.json`. *In progress.* |

## Shared scripts these call

All live in `../shared/`. They belong to the shared framework and are not changed by this category.

| Script | Called by | What it does for load testing |
|---|---|---|
| `start-environment.sh start` | GitHub Actions; `local-run.sh start` | Starts throwaway MySQL (port 3307) and Kafka (19092) with `docker-compose.yml`, creates the four databases, builds and starts Auth (5261), Item (5001), Matching (5179) and Admin Verify (5003) in Development (migrations run, and Auth seeds the user and admin accounts), starts the frontend (5173), and writes `environment.sh` with the service URLs and settings. |
| `start-environment.sh stop` | GitHub Actions; `local-run.sh stop` | Stops the services and removes the Compose containers (no persistent volumes, so the data is gone). Keeps the logs. |
| `start-environment.sh validate-report load-testing` | `run.sh`'s exit handler | Checks `summary.md` and `details.json` exist and are well formed (a non-empty `cases` array, each case with a name, description and valid status, and no failed case with exit code 0), then prints each case. |
| `seed-test-data.sh load-testing` | GitHub Actions; `local-run.sh start` | Loads `environment.sh` and runs this folder's `seed.sh`. |
| `docker-compose.yml` | `start-environment.sh` | MySQL 8.0.40 and Kafka 7.5.0 definitions and health checks |

## How a run fits together

```
GitHub Actions (load-testing job)              local-run.sh (your machine, WSL2)
  start-environment.sh start                     start: start-environment.sh start
  seed-test-data.sh load-testing ──► seed.sh             seed-test-data.sh load-testing ──► seed.sh
  load-testing/run.sh                            run:   fresh ASE_REPORT_ROOT, then load-testing/run.sh
  start-environment.sh stop                      stop:  start-environment.sh stop

load-testing/run.sh
  ├─ install/verify JMeter 5.6.3 (cached)
  ├─ jmeter -n  plans/lost-and-found-load.jmx  -q config/load.properties  (service URLs from environment.sh)
  │     → results.jtl, html/ dashboard
  ├─ node scripts/summarize-results.mjs  → summary.md, details.json (fails if a threshold is broken)
  └─ exit handler → validate-report → status.txt (passed / failed)
```

## Run locally (WSL2)

One-time machine setup: see **Prerequisites → Windows: WSL2 setup** in [`../README.md`](../README.md). Then, from the repository root in a WSL terminal:

```bash
bash testing-pipeline/load-testing/local-run.sh start   # fresh environment + seed (about 1.5 minutes)
bash testing-pipeline/load-testing/local-run.sh run     # run the load tests; repeat as often as needed
bash testing-pipeline/load-testing/local-run.sh stop    # stop the environment when finished
```

`bash testing-pipeline/load-testing/local-run.sh all` does start, run and stop in one go.

**What `local-run.sh` does for you:**
- **`start`** creates a fresh runtime folder, starts the environment, seeds it, and records where it lives (`/tmp/ase-load-testing-environment`). Later commands work from any terminal, without keeping exported variables. It refuses to start a second environment while one is recorded.
- **`run`** loads the recorded environment and gives every run a **fresh report folder** (the runner refuses to overwrite earlier results). It then calls `run.sh`, prints the report folder, and opens the HTML dashboard in the Windows browser. Set `ASE_OPEN_REPORT=false` to skip opening it.
- **`stop`** stops the recorded environment and forgets it.

**Without the helper**, the same steps are:
```bash
export ASE_RUNTIME_DIR="$(mktemp -d -t ase-environment.XXXXXX)"
export ASE_COMPOSE_PROJECT=ase-testing-local
bash testing-pipeline/shared/start-environment.sh start
source "$ASE_RUNTIME_DIR/environment.sh"
bash testing-pipeline/shared/seed-test-data.sh load-testing
export ASE_REPORT_ROOT="$(mktemp -d -t ase-reports.XXXXXX)"   # after sourcing; fresh for every run
bash testing-pipeline/load-testing/run.sh
bash testing-pipeline/shared/start-environment.sh stop
```

Only one environment can run at a time on a machine, because the ports are fixed. To run UI/E2E tests locally as well, `stop` this environment first, so they start on a fresh one.

## Run in GitHub Actions

Actions → **ASE testing pipeline** → Run workflow → `load-testing` (or `all`). The load-testing job runs on its own runner with its own fresh environment, in parallel with mutation and UI/E2E. The `ase-load-testing` artifact contains the reports and application logs.

## Reports

Written to `$ASE_REPORT_ROOT/load-testing/`:

| File | Contents |
|---|---|
| `summary.md` | Run setup, totals, error rate, per-endpoint table, and a pass/fail line per threshold |
| `details.json` | One case per endpoint, per thread group and for the overall error rate, with users, requests, error %, p50/p90/p95/p99, throughput and the threshold result |
| `results.jtl` | JMeter's raw results |
| `html/index.html` | JMeter's HTML dashboard |
| `status.txt`, `console.log` | The runner's status and full console output |

## Assessment-branch note

Auth's login rate limit is raised from 10 to 1000 per 5 minutes on `ASE/Assignment` only, so 20 real users can log in on every run. See "Changes after the original implementation", change 4, in [`../README.md`](../README.md). Production keeps 10 per 5 minutes.
