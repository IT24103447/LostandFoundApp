# Lost & Found App - Initial README

## Branch Strategy
- `main` - protected, production, requires PR + review
- `develop` - active integration branch
- `feature/*` - individual work, PR into `develop`

## Services
- `services/auth-service` - Authentication & role-based access
- `services/item-service` - Lost & found item records
- `services/matching-service` - Lost/found item matching
- `services/admin-verify-service` - Scam/spam moderation

## Frontend (`frontend/`)
React + Vite + TypeScript SPA. Calls each backend service directly via REST — no API gateway.

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
├── api/         — HTTP wrappers that use ../../../lib/apiClient
├── schemas/     — zod schemas mirroring the backend DTOs
├── components/  — feature-specific UI (own UI primitives; only promote to a shared location when ≥2 features use them)
└── pages/       — route targets
```
- Add a new microservice's base URL to `frontend/src/config/env.ts` (and `.env.example`) — typed service map.
- **Do not import from other features/.** Cross-feature code lives in exactly two places: `lib/apiClient.ts` (HTTP primitive) and `App.tsx` (route table).
- New pages register a route by adding a single line to `App.tsx`; nothing else needs touching.
- HTTP primitive lives in `frontend/src/lib/apiClient.ts`; the JWT request interceptor lands here when login is added.

## Current Local Setup (Auth Service)
1. Install .NET 8 SDK
2. `cd services/AuthService`
3. Configure **secrets** via user-secrets (per-machine, never committed). `appsettings.Development.json` is committed with **placeholders only** — real values come from the user-secrets store. See [Local Secrets](#local-secrets) below.
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
# the V002 migration's idempotent ALTER TABLE check (using MySQL session
# variables @col_exists) can execute. Without it, the app will fail to boot
# with "Parameter '@col_exists' must be defined".
dotnet user-secrets set "ConnectionStrings:MySql" "Server=localhost;Database=auth_service;Uid=root;Pwd=<your-mysql-password>;Allow User Variables=true;" --project services/AuthService

# AuthService frontend base URL (for email-verification links).
dotnet user-secrets set "Auth:FrontendBaseUrl"  "http://localhost:5173"                                     --project services/AuthService

# Mailtrap SMTP (dev email catching)
dotnet user-secrets set "Smtp:User"            "<mailtrap-username>"                                        --project services/AuthService
dotnet user-secrets set "Smtp:Password"        "<mailtrap-password>"                                        --project services/AuthService

# SendGrid Webhook ECDSA public key (Settings → Mail Settings → Webhooks → "Signed Event Webhook Requests")
# Paste the PEM contents verbatim (including BEGIN/END markers).
dotnet user-secrets set "SendGrid:WebhookPublicKey" "<paste-pem-here>"                                        --project services/AuthService

# Kafka for local dev (docker compose up -d in infra/kafka)
dotnet user-secrets set "Kafka:BootstrapServers" "localhost:9092"                                           --project services/AuthService
```

Inspect what's stored (read-only): `dotnet user-secrets list --project services/AuthService`

> `Jwt:Secret` must be the **same value** across all 4 services — they share a single token-validation key. Generate once:
> `openssl rand -base64 48` or `[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))` in PowerShell.

Configuration is applied in this precedence (later overrides earlier):
`appsettings.json` → `appsettings.Development.json` → user-secrets (dev) → environment variables (CI / Azure)

## SMTP Provider

- **Dev**: Mailtrap sandbox (`smtp.mailtrap.io`) configured in `appsettings.Development.json`.
- **Prod**: SendGrid SMTP relay (`smtp.sendgrid.net`) configured in `appsettings.json`.
- Same `SmtpEmailService` handles both — switch by environment/user-secrets, no code change needed.
  We use SendGrid in **SMTP relay mode** (`smtp.sendgrid.net:587`, `User=apikey`, `Password=<your API key>`) so
  the existing `SmtpEmailService` works without any code change — just point the SMTP settings at SendGrid.

### Switching dev → SendGrid (for bounce testing)
```powershell
dotnet user-secrets set "Smtp:Host"     "smtp.sendgrid.net"  --project services/AuthService
dotnet user-secrets set "Smtp:Port"     "587"                --project services/AuthService
dotnet user-secrets set "Smtp:User"     "apikey"             --project services/AuthService
dotnet user-secrets set "Smtp:Password" "<your-sendgrid-api-key>" --project services/AuthService
dotnet user-secrets set "Smtp:From"     "<your-verified-sender@example.com>" --project services/AuthService
```

### SendGrid Webhook (bounce detection)

SendGrid → Mail Settings → Webhooks → add an HTTP POST URL pointing at the bounce endpoint.

In production, point it at `https://<your-auth-service>.azurewebsites.net/api/webhooks/sendgrid`.

In local dev, the AuthService is on `localhost` — SendGrid can't reach it. Use **ngrok**:

```powershell
ngrok http 5261
# copy the https://<random>.ngrok.io URL it prints
# in SendGrid dashboard: set the webhook URL to https://<random>.ngrok.io/api/webhooks/sendgrid
```

Then in the AuthService, paste the **ECDSA public key** (Settings → Mail Settings → Webhooks → "Signed Event Webhook Requests" → "Download" or "Copy") into user-secrets:

```powershell
dotnet user-secrets set "SendGrid:WebhookPublicKey" "<paste the full PEM, including BEGIN/END lines>" --project services/AuthService
```

On `bounce`, `dropped`, `spamreport`, or `blocked`, the AuthService:
1. Logs the event to the `email_bounces` audit table (with the raw payload).
2. Marks the user's most recent active verification token as `bounced_at = now()` (so it can't be used or resent against).
3. Clears the user's `last_resent_at` so they can immediately resend to a corrected email without waiting out the cooldown.

The frontend polls `GET /api/auth/verification-status?sessionToken=…` and surfaces a "this email bounced, please correct it" message.

## Environment Variables (Azure App Service)
- `ConnectionStrings__MySql`
- `Jwt__Secret`
- `Kafka__BootstrapServers`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

## CI/CD
GitHub Actions, path-filtered per service. Build + test on push to `develop`, deploy to Azure App Service on merge to `main`. `main` protected, does not allow force pushes or pushes without making a pull request. Pull request has to be approved by atleast one other memeber before push. 