# Back2U Lost & Found App

A lost-and-found platform built as a microservice system: independent services per domain (auth, items, matching, admin moderation), talking to each other asynchronously over Kafka instead of direct service-to-service calls, with a single React SPA as the one frontend for all of them.

## Status (Sprint 1)

Auth Service is built, tested, and deployed end-to-end. Everything else is scaffolded but not yet implemented - expected for this point in the project, not a gap.

| Service | Status |
|---|---|
| **Auth Service** | ✅ Built, tested, deployed - registration, email OTP verification, login/JWT, password reset, profile update, admin user management (list/kick/unkick) |
| Item Service | 🔲 Not started (Sprint 2) |
| Matching Service | 🔲 Not started (Sprint 3) |
| Admin Verify Service | 🔲 Not started (Sprint 4) |
| Frontend | ✅ Deployed - pages for everything Auth Service supports; other services' pages will fail until each is deployed, by design |

## Architecture

Microservices, no API gateway - the frontend calls each service's own base URL directly. Services never call each other synchronously; cross-service signals go through a shared, self-hosted Kafka broker (e.g. `user.verified`, `user.profile_updated`). Each service owns its own MySQL schema.

```
        Azure Static Web App (React + Vite frontend)
                        │  REST, direct per-service
                        ▼
              Auth Service (Azure App Service)
                    │             │
                    ▼             ▼
          MySQL (auth_db)   Kafka (self-hosted, Azure Container Instance)
                    │
                    ▼
          Application Insights (shared across services)
```

Full reasoning behind each infrastructure choice, the deployment topology, and the complete resource inventory live in [`DevOps_Documentation/Sprint1-DevOps-Technical-Documentation.md`](DevOps_Documentation/Sprint1-DevOps-Technical-Documentation.md).

## Frontend (`frontend/`)

React + Vite + TypeScript SPA. Calls each backend service directly via REST - no API gateway.

### Run locally
```powershell
cd frontend
npm install
cp .env.example .env.local   # then edit if your service URLs differ
npm run dev                   # → http://localhost:5173/register
```

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
- **Do not import from other features/.** Cross-feature code lives in exactly two places: `lib/apiClient.ts` (HTTP primitive) and `App.tsx` (route table).
- New pages register a route by adding a single line to `App.tsx`; nothing else needs touching.

## Testing (Auth Service)

`services/AuthService.Tests/` covers the service at two levels:
- **Unit tests** - controllers, validators, and JWT/token logic with every dependency (DB, SMTP, Kafka) mocked via Moq. Fast, no external services required.
- **Integration tests** (`Integration/`) - boot the real app via `WebApplicationFactory<Program>` against a real, disposable MySQL container spun up with **Testcontainers** (requires Docker running locally); only SMTP and Kafka are faked, since those talk to the outside world. Exercises real routing, real SQL, real auth middleware.

```powershell
cd services/AuthService.Tests
dotnet test
```

## Current Local Setup (Auth Service)
1. Install .NET 8 SDK
2. `cd services/AuthService`
3. Configure **secrets** via user-secrets (per-machine, never committed). `appsettings.Development.json` is committed with **placeholders only** - real values come from the user-secrets store. See [Local Secrets](#local-secrets) below.
4. Run local Kafka: `cd infra/kafka && docker compose up -d` (uses `localhost:9092`)
5. `dotnet run`

### Local Secrets (user-secrets, never committed)
The committed `appsettings.Development.json` intentionally ships with **empty / placeholder** secret values.
Each developer overrides them locally using the .NET user-secrets store (stored outside the repo under
`%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\`).

One-time init (already run on this repo): `dotnet user-secrets init --project services/AuthService`

Set each of the following (replace the angle-bracket values with your own real secrets):

```powershell
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

Inspect what's stored (read-only): `dotnet user-secrets list --project services/AuthService`

> `Jwt:Secret` must be the **same value** across all 4 services - they share a single token-validation key. Generate once:
> `openssl rand -base64 48` or `[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))` in PowerShell.

Configuration is applied in this precedence (later overrides earlier):
`appsettings.json` → `appsettings.Development.json` → user-secrets (dev) → environment variables (CI / Azure)

## SMTP Provider

- **Dev**: Mailtrap sandbox (`smtp.mailtrap.io`) configured in `appsettings.Development.json`.
- **Prod**: SendGrid SMTP relay (`smtp.sendgrid.net`), configured via App Service environment variables. `Smtp__User` must be the literal string `apikey` - not an account email - that's SendGrid's fixed SMTP convention.
- Same `SmtpEmailService` handles both - switch by environment, no code change needed. Email sending is deliberately fire-and-forget: failures are logged, never surfaced to the caller, so delivery is confirmed via SendGrid's own Activity dashboard, not the app's API response.

## CI/CD

GitHub Actions, one workflow per service plus one for the frontend, path-filtered so a change to one never triggers another's pipeline.

```
feature/<epic>-service   - every push builds + tests
        ↓ (direct push)
develop                   - build+test AND deploy (the active deploy target)
        ↓ (PR + review required)
main                       - build+test only; clean, reviewed archive, deploy intentionally skipped
```
`main` is protected: no force pushes, no direct pushes, and a PR needs at least one approval before merge.

Full pipeline design, deploy gating rationale, and hard-won gotchas (publish-profile auth, Static Web Apps build quirks, Kafka broker config) are documented in [`DevOps_Documentation/Sprint1-DevOps-Technical-Documentation.md`](DevOps_Documentation/Sprint1-DevOps-Technical-Documentation.md).

## Documentation

Everything DevOps- and infrastructure-related lives in [`DevOps_Documentation/`](DevOps_Documentation/):
- [`Sprint1-DevOps-Technical-Documentation.md`](DevOps_Documentation/Sprint1-DevOps-Technical-Documentation.md) - architecture, infrastructure inventory, database/Kafka/email/observability details, CI/CD design, full functional verification record
- [`Azure-DevOps-Setup-Reference.md`](DevOps_Documentation/Azure-DevOps-Setup-Reference.md) - step-by-step Azure setup instructions, reusable for Item/Matching/Admin Verify Service in later sprints
- [`Sprint1-Deployment-Checklist.md`](DevOps_Documentation/Sprint1-Deployment-Checklist.md) - condensed setup/verification checklist
- [`Secrets-Map-AI-Safe-Reference.md`](DevOps_Documentation/Secrets-Map-AI-Safe-Reference.md) - credential *names and shapes* only, safe to share with AI tools; actual credential values live in a separate, team-internal, never-committed file
