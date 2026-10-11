# Load and performance testing

Apache JMeter load tests for the Lost & Found microservices (Auth, Item, Matching, Admin Verify). This folder is the load-testing category of the ASE testing pipeline. Shared setup, the CI workflow and the reporting contract are described in [`../README.md`](../README.md).

> **Status:** complete. The JMeter plan covers all four services (Auth, Item, Matching, Admin Verify) with three thread groups (steady users, spike users, admin), and the category runs for real (`ASE_SUITE_READY=true`). Coverage is checked on every run. The thresholds are calibrated from three local runs and adjusted after one CI failure; the load-testing job has passed on GitHub Actions since then.

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
| `run.sh` | **The only test entry point**, called by GitHub Actions and locally. It checks the service URLs are set; installs JMeter 5.6.3 once (checksum-verified, cached in `~/.cache/ase-load-testing`, or `ASE_JMETER_CACHE`); runs the plan headless with the environment's hosts and ports; builds the HTML dashboard; then calls the summary script, which checks the thresholds. In GitHub Actions it also adds `summary.md` to the run page. Its exit code fails the run when JMeter fails or a threshold is broken. |
| `seed.sh` | Scenario data. Run by the shared `seed-test-data.sh load-testing` after the environment starts. Logs in the first 6 users through the real API and creates 1 lost plus 1 found listing each (12 total, unique `(seed <runId>)` titles, no photos), so search and view have data from the first request. Two per user stays below Admin Verify's spam rule (3 in 60 minutes). |
| `local-run.sh` | **Local convenience only; CI does not use it.** Wraps the documented local commands (see "Run locally"). |
| `config/load.properties` | The load profile: users, ramp, peak and spike lengths, think times, and `summariser.interval=5` for live console progress. Each value can be overridden with `-J<name>=<value>`. |
| `config/thresholds.json` | Pass/fail limits checked after the run: error rate ≤ 1%; p95 per thread group, calibrated at about 5× the p95 measured over three local runs and adjusted after CI: steady 300 ms (login and create 1000 ms), spike 1000 ms (create 1500 ms; login 5000 ms, because 20 simultaneous BCrypt logins are CPU-bound on a 2–4 core CI runner), admin 1500 ms (login 1000 ms; only about 15 admin requests per run, so its p95 is effectively its slowest request). Its `expected` section lists the thread groups that must run and the endpoints each must call (coverage). |
| `data/users.csv` | `user1`–`user20` login details, one row per user thread (Development seed accounts, test-only passwords) |
| `data/admin.csv` | `admin1` login details |
| `data/search-terms.csv` | Search word, category and type combinations, so searches vary instead of repeating one cached query |
| `plans/lost-and-found-load.jmx` | The JMeter plan: one Test Fragment per service (Auth, Item, Matching, Admin Verify) called through Module Controllers, and three thread groups. **Users - steady** (20, ramp 20 s, peak 25 s, think time 0.5–1.5 s) and **Users - spike** (20 starting within 1 s at 45 s, 15 s, no think time) run the same user journey; **Admin** (1, 60 s, think time 2 s) reads the user list and the Spam Review queue. Every user logs in once through the real API and reuses its JWT. |
| `scripts/summarize-results.mjs` | Reads `results.jtl`, works out statistics overall, per thread group and per endpoint (requests, error %, p50/p90/p95/p99, throughput), checks the thresholds **and coverage** in `config/thresholds.json` (each expected thread group ran and called all its endpoints, so parts of the plan cannot silently not execute), and writes `summary.md` and `details.json`, sorted by thread group and journey order. Exits 1 if any check fails or no results were recorded. |

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

## Why `run.sh` does not start the environment

- In GitHub Actions, the workflow starts the shared environment, runs `run.sh`, and stops the environment as **separate steps**. The framework says runners must not start it again: a second start would collide on the fixed ports.
- Startup takes about 90 seconds and a run about 65 (60 s of load, then under 2 s for the summary and dashboard). Keeping them separate lets you start once, then run as often as needed (for example a warm-up and then the live demonstration).
- `local-run.sh` is the local equivalent of the workflow's steps around `run.sh`: start, then run, then stop. It also remembers the environment across terminals, gives each run a fresh report folder, and opens the dashboard.
- In CI, unit testing runs first. Load testing starts once unit testing has finished (whatever its result), in parallel with mutation and UI/E2E, on its own runner with its own MySQL, Kafka and services.

## Run locally (WSL2)

One-time machine setup: see **Local setup on Windows (WSL2)** in the [DevOps documentation](../../DevOps_Documentation/ASE-Testing-Pipeline.md). Then, from the repository root in a WSL terminal:

```bash
bash testing-pipeline/load-testing/local-run.sh start   # fresh environment + seed (about 1.5 minutes)
bash testing-pipeline/load-testing/local-run.sh run     # run the load tests (about 65 s); repeat as often as needed
bash testing-pipeline/load-testing/local-run.sh stop    # stop the environment when finished
```

`bash testing-pipeline/load-testing/local-run.sh all` does start, run and stop in one go.

The first run on a newly started environment can be slower: after a machine restart, one run saw a ~9 s database write stall. For a demonstration, do one warm-up `run` first.

**What `local-run.sh` does for you:**
- **`start`** creates a fresh runtime folder, starts the environment, seeds it, and records where it lives (`/tmp/ase-load-testing-environment`). Later commands work from any terminal, without keeping exported variables. It refuses to start a second environment while one is recorded. If seeding fails after the environment has started, it stops that environment again, so nothing is left running on the ports.
- **`run`** loads the recorded environment and gives every run a **fresh report folder** (the runner refuses to overwrite earlier results). It then calls `run.sh`, prints the report folder, and opens the HTML dashboard in the Windows browser. Set `ASE_OPEN_REPORT=false` to skip opening it.
- **`stop`** stops the recorded environment and forgets it.
- **Keeping local reports (optional):** set `ASE_LOCAL_REPORTS` to a folder (relative to the repository root or absolute) and each `run` writes to its own `load-report-<date>_<time>/` subfolder there instead of a temporary folder, for example `ASE_LOCAL_REPORTS=QA_Sprint3/ASE-Report/Reports bash testing-pipeline/load-testing/local-run.sh run`. Unset, reports go to a temporary folder as before. CI is unaffected. Choose a git-ignored folder, because generated reports should not be committed.

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

## Live progress while it runs

Every 5 seconds the console prints one line per service (`Auth`, `Item`, `Matching`, `AdminVerify`) plus JMeter's overall `summary` line. Each comes in two forms: `+` for the last interval and `=` for the running total, with requests, rate, average and maximum response time, errors, and active, started and finished users. Each fragment's steps contain a Summariser named after the service, and same-named summarisers combine into one line. The ramp-up, steady peak and the spike at about 45 s are visible as they happen; the endpoint table and dashboard follow when the run ends.

## Reports

Written to `$ASE_REPORT_ROOT/load-testing/`:

| File | Contents |
|---|---|
| `summary.md` | Run setup, totals, error rate, per-endpoint table, and a pass/fail line per threshold |
| `details.json` | One case per endpoint, per thread group and for the overall error rate, with users, requests, error %, p50/p90/p95/p99, throughput and the threshold result |
| `results.jtl` | JMeter's raw results |
| `html/index.html` | JMeter's HTML dashboard |
| `status.txt`, `console.log` | The runner's status and full console output |

## Typical results

A passing local run (WSL2, after a warm-up) is **PASSED (30 checks)**, with about 14,000 requests and 0 errors:

- **Steady users:** p95 is 3–57 ms on every endpoint except login (about 185 ms). Login is slow by design, because BCrypt password hashing is deliberately CPU-heavy.
- **Spike users:** with 20 users arriving at once and no think time, p95 rises to 8–58 ms, except search (about 175 ms, the heaviest query) and login (about 255 ms). There are still 0 errors.
- **Admin:** the two admin reads stay under 15 ms p95 while the user load changes around it.
- **On GitHub's runner:** steady and spike reads are similar, but the spike's 20 simultaneous logins queue on the 2–4 cores (p95 up to about 2.3 s). That is why spike login has its own 5000 ms limit.

## Assessment-branch note

Auth's login rate limit is raised from 10 to 1000 per 5 minutes on `ASE/Assignment` only, so 20 real users can log in on every run. See "Changes after the original implementation", change 4, in the [DevOps documentation](../../DevOps_Documentation/ASE-Testing-Pipeline.md). Production keeps 10 per 5 minutes.
