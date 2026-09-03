# Lost & Found App - Sprint 1 DevOps Technical Documentation

**Service:** Authentication Service
**Sprint:** 1
**Status:** Deployed, verified end-to-end, merged to `main`

---

## 1. Architecture Overview

### 1.1 System design
Microservice architecture, 4 independently deployable services (Auth, Item, Matching, Admin Verify - only Auth built in this sprint), each with its own MySQL schema. No API gateway - the frontend calls each service's App Service URL directly over REST. Services communicate with each other asynchronously via a shared, self-hosted Kafka broker, never via direct synchronous calls to one another.

### 1.2 Why these specific technology choices

| Layer | Choice | Reasoning |
|---|---|---|
| Backend hosting | Azure App Service (Linux, F1 Free) | Native .NET runtime support, zero container overhead, genuinely free at this scale, matches the project's "no Docker except for Kafka" constraint |
| Database | Azure Database for MySQL Flexible Server | Required by project spec; one server, one schema per service for data ownership isolation without 4 separate server costs |
| Event streaming | Self-hosted Apache Kafka (Confluent image), on Azure Container Instances | Required to be genuinely self-hosted (not a managed equivalent like Confluent Cloud or Azure Event Hubs - the latter explicitly doesn't run any Apache Kafka code, per Microsoft's own documentation, and wouldn't satisfy a self-hosted requirement even though it speaks the Kafka wire protocol) |
| Frontend hosting | Azure Static Web Apps | Free, GitHub Actions native, avoided after Netlify's free-tier build-minute allowance was exhausted |
| CI/CD | GitHub Actions, one workflow per service + one for the frontend | Path-filtered so a change to one service never triggers another's pipeline, preserving true independent deployability |
| Auth | JWT, shared secret across all 4 services | Enables stateless cross-service authorization without a gateway or session store |
| Database access | Raw ADO.NET, parameterized SQL - no ORM | Project requirement; full visibility into exact SQL executed, no hidden query generation |

### 1.3 Deployment topology
```
                    ┌─────────────────────────┐
                    │   Azure Static Web App    │
                    │  (React + Vite frontend)  │
                    └────────────┬──────────────┘
                                 │ REST (direct, no gateway)
                                 ▼
                    ┌─────────────────────────┐
                    │   Auth Service            │
                    │   (Azure App Service)     │
                    └──┬──────────┬─────────────┘
                       │          │
              ┌────────▼──┐   ┌───▼──────────────┐
              │ MySQL      │   │ Kafka (ACI)      │
              │ (auth_db)  │   │ self-hosted      │
              └────────────┘   └──────────────────┘
                       │
              ┌────────▼──────────────┐
              │ Application Insights  │
              │ (shared, all services)│
              └────────────────────────┘
```

---

## 2. Infrastructure Inventory

| Resource | Name | Type | Tier/SKU | Cost status |
|---|---|---|---|---|
| Resource group | `lostfound-rg` | Container for all resources | - | Free (no direct cost) |
| App Service Plan | `lostfound-plan` | Compute for App Services | F1 (Free) | Free |
| App Service | `lostfound-auth-service` | Auth Service hosting | Linux, .NET 8 | Free (on F1) |
| MySQL Server | `lostfound-mysql` | Database | Burstable B1MS | Free within free-tier hours (account-dependent) |
| MySQL Schemas | `auth_db`, `item_db`, `matching_db`, `admin_verify_db` | Logical databases | - | No separate cost |
| Container Instance | `lostfound-kafka` | Self-hosted Kafka broker | 1 vCPU / 1.5GB | **Billed continuously** - the project's primary ongoing cost |
| Application Insights | `lostfound-insights` | Telemetry/observability | Web application type | Free tier allowance applies |
| Static Web App | `lostfound-frontend` | Frontend hosting | Free | Free |

**Cost note:** Container Instances has no free tier on any Azure account type. Check **Cost Management + Billing → Cost analysis** in the Portal for actual figures - the `az consumption usage list` CLI command is unreliable/preview and frequently returns blank cost data even when charges are accruing.

---

## 3. Database Layer

### 3.1 Schema (`auth_db`)

| Table | Purpose | Key columns |
|---|---|---|
| `users` | Core account records | `id` (CHAR 36, UUID), `email`, `password_hash`, `name`, `phone_no`, `is_admin`, `is_email_verified`, `is_kicked`, `deleted_at`, `last_resent_at`, `created_at`, `updated_at` |
| `email_verification_tokens` | OTP verification state | `user_id`, `code_hash`, `expires_at`, `attempts`, `used_at` |
| `password_reset_tokens` | Password reset OTP state | `user_id`, `code_hash`, `expires_at`, `attempts`, `used_at` |

### 3.2 Migration process
Migration/seed logic in `Program.cs` is gated to `Development` environment only and never runs automatically on Azure (`ASPNETCORE_ENVIRONMENT=Production` by default on App Service). Migrations for this sprint were consolidated from 7 original incremental scripts into a single combined script, run manually once against the live database:

- Two original scripts (add bounce-tracking, then remove it in the very next migration) were a net no-op and skipped entirely
- All `USE`/`INFORMATION_SCHEMA` references corrected from the scripts' hardcoded `auth_service` to the actual provisioned schema name `auth_db`
- Plain `ALTER TABLE ... ADD COLUMN` statements made idempotent (existence-check pattern) so the combined script is safe to re-run

**Execution method:** direct `mysql` CLI connection (Azure Database for MySQL Flexible Server has no built-in Portal query editor, unlike Azure SQL Database).

### 3.3 Seed data
No automatic seeding in Production. One admin account and one regular user account inserted manually via direct SQL, both marked pre-verified (`is_email_verified = 1`) to allow immediate login testing without requiring the OTP flow. Real bcrypt password hashes generated and verified against the application's actual hashing implementation before insertion, not assumed.

### 3.4 Network security
- `SslMode=Required` enforced on all connections - Flexible Server rejects unencrypted connections by default
- Firewall: `AllowAzureServices` (`0.0.0.0-0.0.0.0`, blanket rule, not IP-pinned, permits App Service traffic regardless of Azure's assigned outbound IP) and an IP-pinned rule for local development access (requires periodic re-adding if the developer's residential IP changes)

---

## 4. Event Streaming (Kafka)

### 4.1 Configuration
Self-hosted `confluentinc/cp-kafka:7.5.0`, single-node KRaft mode (no separate Zookeeper dependency), deployed as an Azure Container Instance with a public IP and stable DNS label.

**Critical configuration detail:** `KAFKA_CONTROLLER_QUORUM_VOTERS` is set to `1@localhost:9093`, not the container's public FQDN. Since broker and controller run as the same process in this single-node setup, routing controller heartbeat traffic through the public internet address instead of loopback causes a crash loop (confirmed root cause of an earlier `CrashLoopBackOff` state with restart counts in the hundreds).

### 4.2 Verified event flows
Two topics confirmed producing and consuming correctly, verified via live consumer inspection with real application traffic:

| Topic | Triggered by | Payload includes |
|---|---|---|
| `auth.user.profile_updated` | User updates their profile | `userId`, `updatedFields`, plus current values for changed fields (email/name/phone) |
| `auth.user.verified` | User completes email OTP verification | `userId`, `email`, `name`, `phone` (full details - this is the intended "user now usable" signal for downstream services) |

**Design note:** no `UserCreated`/`UserRegistered` event exists, and this is intentional, not a gap - an unverified user cannot perform any action other downstream services would need to know about (posting is blocked pre-verification per the login flow's own acceptance criteria), so `UserVerifiedEvent` is the meaningful "user now exists and is usable" signal for consumers, not registration itself.

### 4.3 Known non-blocking log noise
The container's own log shows two categories of recurring noise, both investigated and confirmed not to affect real message delivery:
- **Internal heartbeat timeouts** (broker's controller self-check on `localhost:9093`, every 5–10 minutes) - likely resource contention on a single-vCPU container running broker+controller simultaneously. Confirmed non-fatal: real events were delivered correctly with valid payloads throughout periods where this noise was present.
- **Malformed packets from internal Azure IP ranges** hitting the broker's public port - expected background noise for a `PLAINTEXT`, unauthenticated, publicly-exposed broker, not application traffic.

If either pattern ever correlates with actual message loss, next step is a resource bump to 2 vCPU / 2GB (requires delete + recreate, ACI has no in-place resize).

### 4.4 Cost management
Container Instances bills continuously while running. Stopping (`az container stop`) between work sessions is a standard practice for this project - confirmed safe: stopping/starting the *same* container group (not deleting and recreating it) preserves its public IP and DNS label, so no downstream reconfiguration is needed after a restart. Always re-verify the FQDN matches the App Service's `Kafka__BootstrapServers` setting after any restart as a precaution.

---

## 5. Email Delivery

### 5.1 Provider
SendGrid, single-sender verification (not full domain verification - faster setup, doesn't require owning a dedicated domain). Chosen over Mailtrap's Sending product specifically because Mailtrap Sending requires full DNS domain verification, a materially higher setup cost for a student project timeline.

### 5.2 Configuration facts worth documenting explicitly
- `Smtp__User` must be the literal string `apikey` - not the SendGrid account email. This is SendGrid's fixed SMTP authentication convention, not project-specific configuration.
- The Mailtrap placeholder values present in `appsettings.Development.json` are inert template text (never filled in) and are architecturally incapable of affecting the deployed app - .NET only loads `appsettings.{Environment}.json`, and Production never reads the Development file.

### 5.3 Verification methodology
The application's email service is deliberately fire-and-forget: every send exception is caught, logged as a warning, and the caller still receives a success response regardless of actual delivery outcome. This means:
- **A successful API response does not confirm delivery**
- **The application's own logs only capture send-time exceptions**, never confirm actual receipt, and show nothing at all for a silent server-side rejection (e.g., SendGrid accepting a request and then dropping the message)
- **The only reliable delivery confirmation is SendGrid's own Activity dashboard** (request count, delivered %, bounce %, spam-report %), checked independently of the application

Verified: 2/2 test sends, 100% delivered, 0% bounced, per SendGrid's dashboard.

---

## 6. Observability

### 6.1 Application Insights
Connection string is necessary but **not sufficient** on its own - `builder.Services.AddApplicationInsightsTelemetry()` must be explicitly called in `Program.cs` before `builder.Build()`. This was initially missing; the symptom was not an error of any kind, simply zero telemetry ever received, silently. Diagnosed by attempting a Logs query (`requests | order by timestamp desc`) and receiving `Failed to resolve table or column expression named 'requests'` - indicating the underlying table had never been created at all, not merely empty.

**Environment variable naming is special-cased:** the SDK looks for exactly `APPLICATIONINSIGHTS_CONNECTION_STRING` (single underscore, all caps), bypassing the double-underscore nested-config convention used by every other setting in the project.

**Fixed and verified:** requests, dependencies, and traces now confirmed flowing into the `requests`/`traces` tables following real application traffic.

### 6.2 What's automatically captured
Requests (method, path, status, duration), dependencies (MySQL queries, SMTP calls, Kafka producer calls), unhandled exceptions with stack traces, and any `ILogger` call (`LogWarning`, `LogError`, etc.) as traces. No custom event tracking (`TrackEvent`) is currently instrumented in the codebase.

---

## 7. CI/CD Pipeline Design

### 7.1 Branch strategy
```
feature/<epic>-service   - active development, real CI feedback on every push
        ↓ (direct push, no PR required)
develop                   - active deploy target; build-and-test + deploy both run
        ↓ (PR required, review needed)
main                       - clean, reviewed archive; build-and-test runs, deploy intentionally skipped
```

Rationale for `develop` as the active deploy target rather than `main`: allows real deployed-environment testing before a change is formally reviewed and merged, without requiring a second staging App Service.

### 7.2 Backend pipeline (`auth-service-ci-cd.yml`)
- Path-filtered to both `services/AuthService/**` (the app) and `services/AuthService.Tests/**` (a sibling folder, not nested inside - a filter on only the first would silently never trigger on test-only changes)
- `build-and-test`: restore, build, and run xUnit tests with code coverage collection, on every push to `develop`, `main`, or any `feature/**` branch
- `deploy`: gated to `develop` only via `if: github.ref == 'refs/heads/develop'`; publishes via `azure/webapps-deploy@v3` using a stored publish profile secret

### 7.3 Frontend pipeline (`frontend-ci-cd.yml`)
- Path-filtered to `frontend/**`
- Explicitly builds (`npm ci` + `npm run build`) itself rather than delegating to the deploy action's internal build step - the `Azure/static-web-apps-deploy@v1` action was found to fail with an unhelpfully generic "An unknown exception has occurred" when relying on its own internal Docker-based build process
- Deploy step uses `skip_app_build: true` and points `app_location` directly at the pre-built `frontend/dist` folder - an earlier `app_location`/`output_location` combination was found to sometimes serve the raw, unbuilt source `index.html` (referencing a dev-only Vite path) instead of the actual compiled output
- Build-time environment variables (`VITE_*_API_BASE_URL`) are set directly in the workflow's `env:` block, since Vite bakes these in at build time - this is the authoritative source, not the Static Web App resource's Portal-level environment variable settings, which are secondary

### 7.4 Publish credentials
Newer App Services can have **SCM Basic Auth Publishing Credentials** disabled by default, silently breaking the classic publish-profile deploy method with an unclear error (`Publish profile is invalid for app-name and slot-name provided`). This must be explicitly enabled (Configuration → General settings → Platform settings) - documented here since it's not an obvious first troubleshooting step and cost real debugging time before being identified.

---

## 8. Functional Verification Record

All Auth Service user stories tested against the live deployed environment (not just locally):

| Flow | Result |
|---|---|
| Registration | Account created successfully with valid data |
| Email verification (OTP) | Code delivered, verification succeeds, `is_email_verified` updates correctly |
| Login | Valid JWT issued, correct redirect by role |
| Role-based access control | User vs admin routes correctly enforced |
| Profile update | Changes persist; correct Kafka event published with updated values |
| Password reset | Full flow works; new password active on next login |
| Password hashing | Confirmed bcrypt, no plaintext storage anywhere |
| Admin: kick user | Reversible suspension flag (not a delete); kicked account correctly shown as suspended on login attempt |
| Admin: unkick user | Reverses the kick; account can log in normally again |
| User: delete own account | Hard delete - distinct from the reversible admin kick |

---

## 9. Known Limitations & Deliberate Deferrals

- Item Service, Matching Service, and Admin Verify Service are not yet built (Sprints 2–4). The frontend's corresponding `VITE_*_API_BASE_URL` values are placeholder localhost addresses; any UI feature touching those services will fail until each is deployed in its respective sprint - expected, not a bug.
- No `UserCreated` Kafka event - confirmed intentional design decision (see section 4.2), not an oversight.
- Kafka's internal heartbeat log noise is under observation, not currently actioned, since it hasn't correlated with any real message loss.

---

## 11. AI Assistant Usage

AI tools were used throughout this sprint to support research and planning around software engineering best practices, microservice architecture patterns, and CI/CD pipeline design.

Specific areas where AI assistance was used:
- Researching Azure infrastructure setup patterns (App Service, MySQL Flexible Server, Container Instances, Application Insights, Static Web Apps) and comparing configuration options
- Working through CI/CD pipeline design decisions (branch strategy, path-filtered workflows, deploy gating between `develop` and `main`)
- Diagnosing infrastructure and deployment issues (Kafka broker configuration, publish-profile authentication, frontend build/deploy failures, Application Insights wiring) by discussing symptoms and working through root causes
- Reviewing and refining Kafka event design and microservice communication patterns against the project's own architecture documentation
- Drafting and organizing this documentation set

## 12. Reference Documents

- `Azure-DevOps-Setup-Reference.md` - full step-by-step setup instructions (CLI + Portal) for every piece of infrastructure described here, written so the same setup can be repeated for Item/Matching/Admin Verify Service in later sprints
- `Secrets-Map-AI-Safe-Reference.md` - credential inventory safe to share with AI assistants for troubleshooting (no real values)
- `Secrets-and-Credentials-Reference.md` - actual credential values, team-internal only, never to be uploaded to any AI tool
- `Sprint1-Deployment-Checklist.md` - condensed checklist of every setup and verification item for this sprint, quick reference
