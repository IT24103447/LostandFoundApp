# Lost & Found App - Initial README

## Branch Strategy
- `main` - protected, production, requires PR + review
- `develop` - active integration branch
- `feature/*` - individual work, PR into `develop`

## Services
- `services/auth-service` - Authentication & role-based access

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
dotnet user-secrets set "Jwt:Secret"           "<32+ char random string>"                                  --project services/AuthService
dotnet user-secrets set "ConnectionStrings:MySql" "Server=localhost;Database=auth_service;Uid=root;Pwd=<your-mysql-password>;" --project services/AuthService
dotnet user-secrets set "Smtp:User"            "<mailtrap-username>"                                        --project services/AuthService
dotnet user-secrets set "Smtp:Password"        "<mailtrap-password>"                                        --project services/AuthService
dotnet user-secrets set "Kafka:BootstrapServers" "localhost:9092"                                           --project services/AuthService
```

Inspect what's stored (read-only): `dotnet user-secrets list --project services/AuthService`

> `Jwt:Secret` must be the **same value** across all 4 services — they share a single token-validation key. Generate once:
> `openssl rand -base64 48` or `[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))` in PowerShell.

Configuration is applied in this precedence (later overrides earlier):
`appsettings.json` → `appsettings.Development.json` → user-secrets (dev) → environment variables (CI / Azure)

## Environment Variables (Azure App Service)
- `ConnectionStrings__MySql`
- `Jwt__Secret`
- `Kafka__BootstrapServers`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

## CI/CD
GitHub Actions, path-filtered per service. Build + test on push to `develop`, deploy to Azure App Service on merge to `main`. `main` protected, does not allow force pushes or pushes without making a pull request. Pull request has to be approved by atleast one other memeber before push. 