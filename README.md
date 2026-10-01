# Back2U Lost & Found App

A lost-and-found platform built as a microservice system: independent services per domain (auth, items, matching, admin moderation), talking to each other asynchronously over Kafka instead of direct service-to-service calls (with one narrow, read-only exception, see [Architecture](#architecture)), with a single React SPA as the one frontend for all of them.

## Status (Sprint 3)

Auth, Item and Matching Services are built, tested, and deployed end-to-end. Admin Verify is still scaffolded - expected for this point in the project, not a gap.

| Service | Status |
|---|---|
| Auth Service | ✅ Built, tested, deployed (Sprint 1) - registration, email OTP verification, login/JWT, password reset, profile update, admin user management (list/kick/unkick) |
| Item Service | ✅ Built, tested, deployed (Sprint 2) - report lost/found items with a photo, edit, search/filter, view details, mark resolved, soft-delete, "my reports"; transactional outbox publishing `items.*` events; consumes `matches.confirmed` |
| Matching Service | ✅ Built, tested, deployed (Sprint 3) - AI image descriptions (Gemini), claim preview + scoring, two-sided confirm/reject, contact hand-off, confirmed-match events, email notifications, match re-evaluation and lifecycle handling |
| Admin Verify Service | 🔲 Not started (Sprint 4) |
| Frontend | ✅ Deployed - pages for everything Auth, Item and Matching Services support; Admin Verify pages will fail until that service is deployed, by design |

## Architecture

Microservices, no API gateway - the frontend calls each service's own base URL directly. Cross-service *state changes and signals* go through a shared, self-hosted Kafka broker. Each service owns its own MySQL schema.

```
              Azure Static Web App (React + Vite frontend)
                  │  REST, direct per-service (Bearer JWT)
      ┌───────────┼────────────────────┐
      ▼           ▼                    ▼
 Auth Service   Item Service  ◄──── Matching Service
 (App Service)  (App Service)  REST   (App Service)
      │          │    │       GET     │      │      │
      ▼          ▼    ▼       only    ▼      ▼      ▼
  auth_db     item_db  Blob       matching_db  Gemini  SendGrid
                      Storage                  API     SMTP
      │          │                    │
      └──────────┴──────► Kafka ◄──────┘
                  (self-hosted, Azure Container Instance)
                              │
                              ▼
              Application Insights (shared across services)
```

**Authentication across services.** Auth Service issues the JWT; every other service validates it locally with the shared `Jwt:Secret`/`Issuer`/`Audience` (no call back to Auth). The frontend sends `Authorization: Bearer <jwt>` to Item and Matching Services. Matching Service additionally requires the `email_verified=1` claim (policy `VerifiedClaimUser`).

**Where Matching Service does call another service synchronously.** To preview or submit a claim, Matching Service reads the two reports from Item Service over REST (`GET /api/items/{lost|found}/{id}` and `/mine`), forwarding the caller's own JWT so Item Service's normal authorization applies. This is read-only - Matching never writes to Item Service over REST. Every *write* that crosses the boundary (e.g. resolving the two reports after a confirmed match) is a Kafka event.

### Kafka topics

Topic names are `{TopicPrefix}.{event}`; delivery is at-least-once, so consumers de-duplicate on `EventId`.

| Topic | Producer | Consumers | Purpose |
|---|---|---|---|
| `auth.user.verified` / `.profile_updated` / `.kicked` / `.unkicked` / `.deleted` | Auth | Matching (notification contacts, only when `Notifications:Enabled`) | Keep a local email/active/deleted copy of each user for notifications |
| `items.lost_item.created` / `.updated` | Item | Matching | Trigger image-description jobs and match re-evaluation |
| `items.found_item.created` / `.updated` | Item | Matching | Same, for found reports |
| `items.lost_item.resolved` / `items.found_item.resolved` | Item | Matching | Deactivate matches on a resolved report |
| `items.item.delete_requested` | Item (lost *and* found delete) | Matching | Deactivate matches on a deleted report |
| `matches.confirmed` | Matching | Item | Both parties confirmed - Item Service marks both reports `RESOLVED` |

Reliability patterns used by both new services:

- **Item Service - transactional outbox.** Controllers write the event to `outbox_events` in the same DB transaction as the item change; `OutboxRelayService` publishes to Kafka with retry/backoff and a short lease so several instances never double-publish.
- **Matching Service - confirmation outbox.** `matches.confirmed` is written to `match_confirmation_outbox` in the same transaction as the match reaching `CONFIRMED`, then published by `MatchConfirmationPublisher`.
- **Item Service - inbox.** `match_resolution_inbox` makes `matches.confirmed` handling idempotent; the Kafka offset is committed only after the DB transaction commits.

Item events carry `HiddenInformation` (the owner-only verification detail) for Matching's use. See [Known limitations](#known-limitations) for the broker-security caveat.

Full reasoning behind each infrastructure choice, the deployment topology, and the complete resource inventory live in `DevOps_Documentation/`:
`Sprint1-DevOps-Technical-Documentation.md` (shared infra, Auth) and `Sprint2-DevOps-Technical-Documentation.md` (Item Service, Blob Storage, cross-service auth).

## Item Service (`services/ItemService`)

.NET 8 Web API, raw ADO.NET (parameterized SQL, no ORM), MySQL `item_db`, Azure Blob Storage for photos.

**Data model.** `lost_items`, `lost_item_photos`, `found_items`, `found_item_photos` (soft-delete via `deleted_at`), plus `outbox_events` and `match_resolution_inbox`. Statuses: `ACTIVE`, `MATCHED`, `CLOSED`, `RESOLVED`. Categories: Electronics, Bags, Clothing, Accessories, Documents, Keys, Other (mirrored in the frontend's `reportLostItemSchema.ts`).

**API** (all `[Authorize]`; the reporter's id comes from the JWT `sub` claim):

| Method & route | Purpose |
|---|---|
| `GET /api/items` | Browse/search active items - `q`, `category`, `type` (LOST/FOUND), `dateFrom`, `dateTo`, `page`, `pageSize` (1-50) |
| `GET /api/items/{id}` | Public item details (never includes `HiddenInformation`); resolved items are visible only to their reporter |
| `POST /api/items/lost` · `POST /api/items/found` | Report an item (multipart form, optional single photo: JPEG/PNG/WEBP, ≤ 5 MB, file-signature checked) |
| `GET /api/items/{lost\|found}/{id}` | Report details |
| `PUT /api/items/{lost\|found}/{id}` | Edit own report |
| `PUT` / `DELETE /api/items/{lost\|found}/{id}/photo` | Replace / remove the photo |
| `POST /api/items/{lost\|found}/{id}/resolve` | Mark resolved (reporter only; `409` unless `ACTIVE`) |
| `DELETE /api/items/{lost\|found}/{id}` | Soft-delete (reporter only; `409` if `MATCHED`) |
| `GET /api/items/{lost\|found}/mine` | The caller's own reports |

Non-reporters get `403`, unknown/soft-deleted ids get `404`, unauthenticated calls get `401`.

**Storage guard.** In Production the service refuses to start without `BlobStorage:ConnectionString` rather than falling back to local disk. In Development it falls back to `wwwroot/photos`.

## Matching Service (`services/MatchingService`)

.NET 8 Web API, raw ADO.NET, MySQL `matching_db`, Google Gemini for image descriptions, SMTP for email.

### How a match works

1. **Describe photos.** On `items.*.created/updated` events, an `image_descriptions` job (lease + retry/backoff, max 5 attempts) asks Gemini for a structured description of the item photo (object type, colours, materials, brand, patterns, condition, distinctive features). The prompt treats text inside the image as untrusted and forbids reproducing personal data. Gated by `ImageProcessing:Enabled`.
2. **Preview.** A verified user who owns *one* of the two reports (and not both) previews a lost/found pair. The score (`text-v1`) is a weighted word-overlap similarity: title 20, category 15, description 20, and - when both reports have photos - image description 30 and image attributes 15, normalised by the weights in use. **Threshold: 60.** The response includes the breakdown and a `previewVersion` hash.
3. **Claim.** Submitting requires that `previewVersion` to still match (otherwise `409` - preview again), a score ≥ 60 (`422` otherwise), both reports `ACTIVE`, and that neither report already has a confirmed match. The match starts as `LOST_REPORTER_CONFIRMED` or `FINDER_CONFIRMED` depending on who claimed; the claimant's contact details are captured from their JWT.
4. **Counterpart decides.** The other party confirms or rejects (`CONFIRMED` / `REJECTED`); every decision is written to `match_actions` for audit.
5. **Confirmed.** Both parties can fetch each other's contact details (`return-contact`, confirmed + active matches only). `matches.confirmed` is published through the outbox and Item Service resolves both reports.
6. **Keep matches honest.** Edited reports are re-scored by a `match_reevaluation_jobs` worker (matches that no longer qualify are deactivated, and the job waits for pending photo descriptions); resolved or deleted reports deactivate their matches via `matching_item_states`.
7. **Notify.** Email notifications (`COUNTERPART_ACTION`, `MATCH_CONFIRMED`, `MATCH_REJECTED`) are queued in `match_notifications` and sent by a worker with retry; recipient emails come from `notification_contacts`, kept current by the `auth.user.*` events. Gated by `Notifications:Enabled`.

**API** (all require a verified-email JWT):

| Method & route | Purpose |
|---|---|
| `GET /api/matches/candidates?type=LOST\|FOUND` | The caller's own active reports that can be put forward |
| `POST /api/matches/preview` | Score a lost/found pair |
| `POST /api/matches/claim` | Submit a claim (`201`) |
| `GET /api/matches/mine` | Matches the caller is part of |
| `GET /api/matches?section=…&page=&size=` | Paged list; sections: `active`, `waiting-on-you`, `waiting-on-other`, `confirmed`, `rejected`, `deactivated`, `closed`, `all` |
| `GET /api/matches/{matchId}` | Match details (participants only) |
| `POST /api/matches/{matchId}/finder/confirm` · `/reject` · `GET …/return-contact` | Finder's decision / contact (when the lost reporter claimed first) |
| `POST /api/matches/{matchId}/lost-reporter/confirm` · `/reject` · `GET …/return-contact` | Lost reporter's decision / contact (when the finder claimed first) |
| `GET /health` | Liveness only |

**Data model** (`matching_db`, migrations `001`-`011`): `image_descriptions`, `matches`, `match_actions`, `match_confirmation_outbox`, `notification_contacts`, `match_notifications`, `match_notification_activation`, `matching_item_states`, `matching_item_lifecycle_events`, `matching_item_snapshots`, `match_reevaluation_jobs`.

## Frontend (`frontend/`)

React + Vite + TypeScript SPA. Calls each backend service directly via REST - no API gateway.

### Run locally
```bash
cd frontend
npm install
cp .env.example .env.local   # then edit if your service URLs differ
npm run dev                   # → http://localhost:5173/register
```

Default local service URLs (from `.env.example` and each service's `launchSettings.json`): Auth `http://localhost:5261`, Item `http://localhost:5001`, Matching `http://localhost:5179`, Admin Verify `http://localhost:5003` (placeholder). The app checks all four `VITE_*_API_BASE_URL` values at startup, so keep a value for each even when a service isn't running.

### Pages by service

| Feature folder | Routes |
|---|---|
| `features/auth` | `/login`, `/register`, `/verify-email`, `/forgot-password`, `/reset-password`, `/profile` |
| `features/items` | `/report-lost-item`, `/report-found-item` (+ `/success`), `/my-reports`, `/edit-lost-item/:id`, `/edit-found-item/:id`, `/items/:id` (details + claim dialog) |
| `features/matches` | `/matched-items`, `/matched-items/:matchId` (review, confirm/reject, return contact) |
| `features/home` | `/` (browse/search) |
| `features/admin` | `/admin/*` - user management today; moderation arrives with Admin Verify (Sprint 4) |

### Folder conventions (multi-team)
Each microservice owns one folder under `frontend/src/features/<service>/` with this internal layout:

```
features/<service>/
├── api/         - HTTP wrappers that use ../../../lib/apiClient
├── schemas/     - zod schemas mirroring the backend DTOs
├── components/  - feature-specific UI (own UI primitives; only promote to a shared location when ≥2 features use them)
└── pages/       - route targets
```

- Add a new microservice's base URL to `frontend/src/config/env.ts` (and `.env.example`) - typed service map.
- Do not import from other `features/`. Cross-feature code lives in exactly two places: `lib/apiClient.ts` (HTTP primitive) and `App.tsx` (route table).
- New pages register a route by adding a single line to `App.tsx`; nothing else needs touching.
- `apiClient` sends cookies to Auth Service and `Authorization: Bearer <jwt>` to every other service; `AuthContext` holds the token in memory (Auth Service returns it in login/verify/reset/`me` response bodies).

## Testing

Each service has an xUnit project at two levels, plus browser and load tests for the Item/Matching epics.

- **Unit tests** - controllers, validators, scoring, repositories' logic, and workers with dependencies mocked via Moq. Fast, no external services.
- **Integration tests** - boot the real app via `WebApplicationFactory<Program>` against a disposable MySQL container (Testcontainers); the Item and Matching suites also start a Kafka container for the producer/consumer and outbox paths (**Docker must be running**). SMTP and Gemini are faked, except for an optional Mailtrap-backed notification test.
- **Selenium (Chrome)** - drive the real frontend. Item Service stories: report lost/found, edit, search/filter, resolve, view details, delete, my reports. Matching: the claim-and-match flow and the matched-items page. Need the frontend, Auth, Item (and Matching) running locally - see `README.md` in each Selenium project for the required `SELENIUM_*` variables. Never commit real passwords.
- **JMeter plans** (`01`-`11-*.jmx`) - create/search/resolve/delete load, claim preview and duplicate-claim races, match listing, decision/confirm races, and deactivated-match listing.

```bash
cd services/AuthService.Tests     && dotnet test
cd services/ItemService.Tests     && dotnet test      # Docker required
cd services/MatchingService.Tests && dotnet test      # Docker required
```

Item Service's per-story `--filter` commands are in `services/ItemService.Tests/README.md`. The Item Service coverage report (Sprint 2) showed ~81% line coverage.

## Local Setup

Install the .NET 8 SDK, then run local Kafka once for all services: `cd infra/kafka && docker compose up -d` (uses `localhost:9092`). Configuration is applied in this precedence (later overrides earlier): `appsettings.json` → `appsettings.Development.json` → user-secrets (dev) → environment variables (CI / Azure).

Development runs apply the SQL migrations automatically (`DbInitializer`). **Azure does not** - migrations are applied manually with the `mysql` CLI, see `DevOps_Documentation/`.

### Auth Service
`cd services/AuthService`, set the secrets below, `dotnet run`.

### Item Service
```bash
cd services/ItemService
dotnet user-secrets set "Jwt:Secret"              "<same value as Auth Service>"        # must match Auth
dotnet user-secrets set "ConnectionStrings:MySql" "Server=localhost;Database=item_service;Uid=root;Pwd=<pw>;Allow User Variables=true;"
dotnet user-secrets set "Kafka:BootstrapServers"  "localhost:9092"
dotnet run                                         # → http://localhost:5001
```
Blob Storage is optional locally (photos fall back to `wwwroot/photos`). In Production set `BlobStorage__ConnectionString`, `BlobStorage__ContainerName` (`lostfound-item-images`) and `BlobStorage__PublicBaseUrl`.

### Matching Service
```bash
cd services/MatchingService
dotnet user-secrets set "Jwt:Secret"              "<same value as Auth Service>"
dotnet user-secrets set "Jwt:Issuer"              "auth-service"
dotnet user-secrets set "Jwt:Audience"            "lostandfound-app"
dotnet user-secrets set "ConnectionStrings:MySql" "Server=localhost;Database=matching_service;Uid=root;Pwd=<pw>;Allow User Variables=true;"
dotnet user-secrets set "ItemService:BaseUrl"     "http://localhost:5001"   # must be HTTPS outside Development
dotnet run                                         # → http://localhost:5179
```
The service fails fast at startup if the JWT settings, `ItemService:BaseUrl`, or `Kafka:BootstrapServers` / `Kafka:GroupId` are missing. Two feature switches are **off by default**; turn them on only when you have the credentials:

| Setting | Enables | Also requires |
|---|---|---|
| `ImageProcessing:Enabled=true` | Gemini image descriptions | `Gemini:ApiKey`, `Gemini:Model`, `BlobStorage:AllowedHost` (blob hostname only), valid `ImageProcessing:*` timings |
| `Notifications:Enabled=true` | Email notifications | `Smtp:Host`, `Smtp:User`, `Smtp:Password`, `Smtp:FromAddress` (optional `Smtp:FromName`, `Smtp:Port`, default 587) |

Without photo descriptions, claim scoring still works - it just uses only title, category and description. Optional: `Kafka:TopicPrefix` (default `items`), `Kafka:LifecycleGroupId`.

### Local Secrets (user-secrets, never committed)
Committed `appsettings.Development.json` files ship with empty or placeholder secret values. Each developer overrides them locally using the .NET user-secrets store (kept outside the repo under `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\`). One-time init: `dotnet user-secrets init --project services/<Service>`. Inspect what's stored (read-only): `dotnet user-secrets list --project services/<Service>`.

Auth Service values:

```bash
# JWT secret
dotnet user-secrets set "Jwt:Secret"           "<32+ char random string>"                                  --project services/AuthService

# MySQL connection string. NOTE: `Allow User Variables=true` is required so
# the migration's idempotent ALTER TABLE check (using MySQL session
# variables @col_exists) can execute. Without it, the app will fail to boot
# with "Parameter '@col_exists' must be defined".
dotnet user-secrets set "ConnectionStrings:MySql" "Server=localhost;Database=auth_service;Uid=root;Pwd=<your-mysql-password>;Allow User Variables=true;" --project services/AuthService

# AuthService frontend base URL (for email-verification links).
dotnet user-secrets set "Auth:FrontendBaseUrl"  "http://localhost:5173"                                     --project services/AuthService

# Mailtrap SMTP (dev email catching)
dotnet user-secrets set "Smtp:User"            "<mailtrap-username>"                                        --project services/AuthService
dotnet user-secrets set "Smtp:Password"        "<mailtrap-password>"                                        --project services/AuthService

# Kafka for local dev (docker compose up -d in infra/kafka)
dotnet user-secrets set "Kafka:BootstrapServers" "localhost:9092"                                           --project services/AuthService
```

`Jwt:Secret` must be the same value across all 4 services - they share a single token-validation key. Generate once: `openssl rand -base64 48` or `[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))` in PowerShell.

### SMTP Provider
- Dev: Mailtrap sandbox (`smtp.mailtrap.io`).
- Prod: SendGrid SMTP relay (`smtp.sendgrid.net`), configured via App Service environment variables. `Smtp__User` must be the literal string `apikey` - not an account email - that's SendGrid's fixed SMTP convention. `Smtp__FromAddress` (Matching Service) must be a sender address verified in SendGrid, otherwise notifications stay unsent - this was the cause of the Sprint 3 delivery failures.
- Auth Service's `SmtpEmailService` is deliberately fire-and-forget: failures are logged, never surfaced to the caller, so delivery is confirmed via SendGrid's Activity dashboard. Matching Service instead tracks every email in `match_notifications` (`PENDING` → `SENT` / `FAILED`, with `sent_at`) and retries.

## CI/CD

GitHub Actions, one workflow per service plus one for the frontend (`auth-`, `item-`, `matching-service-ci-cd.yml`, `frontend-ci-cd.yml`), path-filtered (each backend filter covers both the service and its `.Tests` project) so a change to one never triggers another's pipeline.

```
feature/<epic>-service   - every push builds + tests
        ↓ (direct push)
develop                   - build+test AND deploy (the active deploy target)
        ↓ (PR + review required)
main                       - build+test only; clean, reviewed archive, deploy intentionally skipped
```

`main` is protected: no force pushes, no direct pushes, and a PR needs at least one approval before merge.

| Service | Azure App Service | Publish-profile secret | Database |
|---|---|---|---|
| Auth | `lostfound-auth-service` | `AZURE_AUTH_SERVICE_PUBLISH_PROFILE` | `auth_db` |
| Item | `lostfound-item-service` | `AZURE_ITEM_SERVICE_PUBLISH_PROFILE` | `item_db` |
| Matching | `lostfound-matching-service` | `AZURE_MATCHING_SERVICE_PUBLISH_PROFILE` | `matching_db` |

All three share the `lostfound-plan` App Service plan, one MySQL Flexible Server, the Kafka container, and the `lostfound-insights` Application Insights resource. The frontend workflow builds `frontend/dist` itself and bakes in each service's production URL (`VITE_*_API_BASE_URL`); `VITE_ADMIN_API_BASE_URL` stays a localhost placeholder until Sprint 4.

Full pipeline design, deploy gating rationale, and hard-won gotchas (publish-profile auth, Static Web Apps build quirks, Kafka broker config) are documented in `DevOps_Documentation/`.

## Known limitations

- **Kafka is `PLAINTEXT` and publicly reachable**, yet create/update/resolve events carry `HiddenInformation`. Accepted for now as a deliberate design for the privileged Matching consumer; anyone who can reach the broker can read these events.
- **Matching → Item REST dependency.** Claim preview/submit and `candidates` fail with `503` while Item Service is unreachable. The Kafka-driven paths (image descriptions, lifecycle, re-evaluation, confirmation) keep working independently.
- **`MATCHED` is a reserved item status.** Item Service guards delete/resolve against it, but nothing sets it yet: a confirmed match moves both reports straight to `RESOLVED`.
- **Kafka cost and availability.** The broker's Container Instance is the only continuously-billed resource and may be stopped between demos; check it before any event-dependent test.
- **Kafka metadata-refresh warnings** appear intermittently in Matching Service logs; delivery was verified unaffected.
- **Failed Gemini jobs** are retried with backoff up to 5 attempts, then stay `FAILED`; a periodic sweep is not implemented yet.
- **Blob container is public-read** by design (stable image URLs); tightening needs SAS URLs and a code change.
- **Swagger is development-only**; a `404` on `/swagger` in Azure is expected.
- Message ordering is not guaranteed (random keys); consumers must not rely on per-item event sequence.

## Documentation

Everything DevOps- and infrastructure-related lives in `DevOps_Documentation/`:

- `Sprint1-DevOps-Technical-Documentation.md` - architecture, infrastructure inventory, database/Kafka/email/observability details, CI/CD design, full functional verification record (Auth Service)
- `Sprint2-DevOps-Technical-Documentation.md` - Item Service: Blob Storage, `item_db` migrations, topic naming, cross-service Bearer auth, verification record
- `Sprint2-Deployment-Checklist.md` / `Sprint1-Deployment-Checklist.md` - condensed setup/verification checklists
- `Sprint3-DevOps-Technical-Documentation.md` - Matching Service: `matching_db` migrations, Kafka consumers/producers and the confirmation outbox, Gemini and SendGrid configuration, CI/CD, functional verification record
- `Sprint3-Matching-Service-Deployment-Checklist.md` - Matching Service deployment, Kafka/Gemini/SendGrid verification, issues found and resolved
- `Azure-DevOps-Setup-Reference.md` - step-by-step Azure setup instructions, reusable for Admin Verify Service in Sprint 4
- `azure-item_db-migration*.sql` - consolidated, manually-applied migrations for Azure `item_db`
- `Secrets-Map-AI-Safe-Reference.md` - credential names and shapes only, safe to share with AI tools; actual credential values live in a separate, team-internal, never-committed file