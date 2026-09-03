# Lost & Found App — Azure Setup & GitHub CI/CD Reference

This document covers the full DevOps setup for the Auth Service (Sprint 1) and the frontend, and the pattern to repeat for each subsequent backend service. Every Azure step includes both a **CLI (PowerShell)** option and a **Portal (GUI)** option. This reflects the actual current, working setup — including every real hurdle hit and how it was fixed, so the same issues don't need re-debugging for Item/Matching/Admin Verify Service in later sprints.

---

## 0. Prerequisites

- Azure account with an active subscription
- Azure CLI installed (`az --version` to check)
- Docker Desktop installed and running (for local Kafka and for any `docker run` diagnostic commands)
- GitHub repo already created, with `main` and `develop` branches

Log into Azure CLI once per session:
```powershell
az login
```

---

## 1. Resource Group & App Service Plan

### CLI
```powershell
az group create --resource-group lostfound-rg --location southeastasia
az appservice plan create --name lostfound-plan --resource-group lostfound-rg --sku F1 --is-linux
```

### Portal (GUI)
1. Search **"Resource groups"** → **+ Create**
2. Name: `lostfound-rg`, Region: Southeast Asia → **Review + Create**
3. Search **"App Service Plans"** → **+ Create**
4. Resource group: `lostfound-rg`, Name: `lostfound-plan`, Operating System: **Linux**, Pricing tier: **F1 (Free)** → **Review + Create**

**Note:** F1 (Free) does not support VNet Integration. Not needed — architecture uses direct frontend-to-service REST calls, no API gateway.

---

## 2. Azure Database for MySQL Flexible Server

One physical server, one separate schema per microservice.

### CLI
```powershell
az mysql flexible-server create --resource-group lostfound-rg --name lostfound-mysql --admin-user <admin-username> --admin-password <admin-password>

az mysql flexible-server db create --resource-group lostfound-rg --server-name lostfound-mysql --database-name auth_db
az mysql flexible-server db create --resource-group lostfound-rg --server-name lostfound-mysql --database-name item_db
az mysql flexible-server db create --resource-group lostfound-rg --server-name lostfound-mysql --database-name matching_db
az mysql flexible-server db create --resource-group lostfound-rg --server-name lostfound-mysql --database-name admin_verify_db
```

### Portal (GUI)
1. Search **"Azure Database for MySQL flexible servers"** → **+ Create**
2. Resource group: `lostfound-rg`, Server name: `lostfound-mysql`, Region: same as resource group
3. Set admin username/password, Compute + storage: **Burstable, B1MS** (lowest tier)
4. After creation → **Databases** → **+ Add** → create `auth_db`, `item_db`, `matching_db`, `admin_verify_db`

**Note:** Azure Database for MySQL Flexible Server has **no built-in Portal query editor** (unlike Azure SQL Database). Migrations and manual queries must be run via a real `mysql` client (CLI, installed via `winget install Oracle.MySQL` if not already present) or MySQL Workbench.

### Firewall rules (required — without these, nothing can reach the DB)

```powershell
az mysql flexible-server firewall-rule create --resource-group lostfound-rg --name lostfound-mysql --rule-name AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
az mysql flexible-server firewall-rule create --resource-group lostfound-rg --name lostfound-mysql --rule-name AllowMyLaptop --start-ip-address <your-public-ip> --end-ip-address <your-public-ip>
```
Get your public IP: `(Invoke-WebRequest -Uri "https://ifconfig.me" -UseBasicParsing).Content` or just search "what's my ip".

**`AllowAzureServices` (`0.0.0.0-0.0.0.0`) is a blanket rule, not IP-pinned** — it lets the App Service connect regardless of what outbound IP Azure assigns it, and never goes stale. **`AllowMyLaptop` is IP-pinned** and *will* go stale if your home/residential IP changes (common — these are usually dynamic). If a local `mysql` connection suddenly fails with `ERROR 2003 ... Can't connect` (error 10060, a timeout — not an authentication failure), re-check and re-add your current IP; the two rules are independent and adding/updating one never affects the other.

### Portal equivalent for firewall
1. `lostfound-mysql` → **Networking**
2. Tick **"Allow public access from any Azure service within Azure to this server"**
3. **"Add current client IP address"**
4. **Save**

### Connection string format
```powershell
az mysql flexible-server show --name lostfound-mysql --resource-group lostfound-rg --query fullyQualifiedDomainName -o tsv
```
```
Server=<fqdn>;Database=<db_name>;Uid=<admin-username>;Pwd=<password>;SslMode=Required;
```
`SslMode=Required` is mandatory — Flexible Server rejects unencrypted connections by default.

---

## 3. Migrations & Seed Data

The app's migration/seed logic in `Program.cs` is gated behind `if (app.Environment.IsDevelopment())` — it **never runs automatically** on Azure, since App Service defaults to `ASPNETCORE_ENVIRONMENT=Production`. Migrations must be run manually, once, per service, against the real Azure database.

### Connect
```powershell
mysql -h lostfound-mysql.mysql.database.azure.com -u <admin-username> -p auth_db
```

### Known mismatch: migration scripts hardcode the wrong DB name
The original scripts do `CREATE DATABASE IF NOT EXISTS auth_service; USE auth_service;` — but the real provisioned schema is `auth_db`. Every `USE` and `INFORMATION_SCHEMA.TABLE_SCHEMA = '...'` reference needs to say `auth_db`, not `auth_service`, before running.

### Combine and clean up before running
Two of the seven original migration files were an add-then-immediately-remove pair (bounce tracking added, then dropped in the very next migration) — net zero effect, safe to skip both entirely rather than run and undo. Combine the remaining ones into a single script with `USE auth_db;` at the top, and make any plain `ALTER TABLE ... ADD COLUMN` idempotent (wrap in an `INFORMATION_SCHEMA.COLUMNS` existence check, same pattern already used for the `last_resent_at` column) so the whole combined script is safe to re-run if something fails partway through.

Run it:
```powershell
mysql -h lostfound-mysql.mysql.database.azure.com -u <admin-username> -p auth_db < combined_migration.sql
```
If PowerShell's `<` redirection misbehaves:
```powershell
Get-Content combined_migration.sql | mysql -h lostfound-mysql.mysql.database.azure.com -u <admin-username> -p auth_db
```

Verify:
```sql
USE auth_db;
SHOW TABLES;
DESCRIBE users;
```

### Seed data
Since nothing auto-seeds in Production, insert at least one admin and one regular user manually. Get the real bcrypt hash and exact column list from the actual model/hasher code first — don't guess field names or order.

```sql
INSERT INTO users (id, email, password_hash, name, phone_no, is_admin, is_email_verified, is_kicked)
VALUES (UUID(), 'admin1@lostandfound.com', '<real-bcrypt-hash>', 'Admin One', '+94770000001', 1, 1, 0);
```
`UUID()` produces MySQL's standard lowercase dashed format — matches what `MySqlConnector`'s `reader.GetGuid()` expects against a `CHAR(36)` column, same shape .NET's `Guid` produces. Mark seeded accounts pre-verified (`is_email_verified = 1`) to skip the OTP flow for initial testing. Rotate any seeded password before a real handoff/demo — it now lives in a real deployed DB, not a throwaway local one.

---

## 4. App Service (one per microservice)

### CLI
```powershell
az webapp create --name lostfound-auth-service --resource-group lostfound-rg --plan lostfound-plan --runtime "DOTNETCORE:8.0"
```

### Portal (GUI)
1. Search **"App Services"** → **+ Create → Web App**
2. Resource group: `lostfound-rg`, Name: `lostfound-auth-service`
3. Publish: **Code**, Runtime stack: **.NET 8 (LTS)**, Operating System: **Linux**
4. App Service Plan: `lostfound-plan`
5. **Review + Create**

Repeat for `lostfound-item-service`, `lostfound-matching-service`, `lostfound-adminverify-service` in later sprints.

### Enable publish-profile deploys (required, and not on by default)
Newer App Services can have **SCM Basic Auth Publishing Credentials** disabled by default — this silently breaks the classic publish-profile deploy method used in section 10, with symptoms like `Download publish profile` erroring in the Portal, or GitHub Actions failing with `Publish profile is invalid for app-name and slot-name provided`.

**Fix, do this right after creating the App Service:**
1. App Service → **Configuration → General settings → Platform settings**
2. **SCM Basic Auth Publishing Credentials** → **On** → **Save**

---

## 5. Application Insights (shared across all services)

### CLI
```powershell
az monitor app-insights component create --app lostfound-insights --location southeastasia --resource-group lostfound-rg --application-type web
az monitor app-insights component show --app lostfound-insights --resource-group lostfound-rg --query connectionString -o tsv
```

### Portal (GUI)
1. Search **"Application Insights"** → **+ Create**
2. Resource group: `lostfound-rg`, Name: `lostfound-insights`
3. **Review + Create**
4. **Overview** → copy the **Connection String**

**Note:** creating this may require registering `Microsoft.OperationalInsights` (a Log Analytics dependency):
```powershell
az provider register --namespace Microsoft.OperationalInsights
az provider show --namespace Microsoft.OperationalInsights --query registrationState -o tsv
```
Wait until `Registered`, then retry.

### Critical: the connection string alone does nothing without a code change
Referencing the `Microsoft.ApplicationInsights.AspNetCore` NuGet package in the `.csproj` and setting the connection string as an App Service setting is **not sufficient**. `Program.cs` must explicitly call:
```csharp
builder.Services.AddApplicationInsightsTelemetry();
```
before `builder.Build()`, and not inside any environment conditional that would exclude Production. Without this line, App Insights receives zero telemetry — no error is thrown anywhere, it just silently never sends anything, and a Logs query like `requests | order by timestamp desc` will fail with `Failed to resolve table or column expression named 'requests'` (not "no rows" — the table doesn't exist at all, because nothing's ever been received).

**The connection string's environment variable name is special-cased** — the SDK looks for one exact name, `APPLICATIONINSIGHTS_CONNECTION_STRING` (all caps, single underscore, no colons, no double-underscore nesting), bypassing the normal nested-config-binding convention every other setting in this project uses. Confirm it's set under that exact name:
```powershell
az webapp config appsettings list --name lostfound-auth-service --resource-group lostfound-rg --query "[?contains(name, 'ApplicationInsights') || contains(name, 'APPLICATIONINSIGHTS')]"
```

### Verifying telemetry is actually flowing
**Live Metrics** only shows data while actively watching in real time — easy to miss due to timing. More reliable: **Logs**, run after triggering real traffic (login, register) and allowing a short ingestion delay:
```kusto
requests
| order by timestamp desc
| take 20
```
Real rows appearing confirms it's genuinely working.

---

## 6. Kafka (self-hosted, containerized)

Kafka can't run natively on App Service (needs a persistent broker process) — this is the **only** containerized piece; everything else runs natively on App Service.

### 6a. Local Kafka (Docker Compose)

`infra/kafka/docker-compose.yml`:
```yaml
services:
  kafka:
    image: confluentinc/cp-kafka:7.5.0
    container_name: kafka
    ports:
      - "9092:9092"
    environment:
      KAFKA_NODE_ID: 1
      KAFKA_PROCESS_ROLES: broker,controller
      KAFKA_LISTENERS: PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093
      KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092
      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT
      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_CONTROLLER_QUORUM_VOTERS: 1@kafka:9093
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_AUTO_CREATE_TOPICS_ENABLE: "true"
      CLUSTER_ID: MkU3OEVBNTcwNTJENDM2Qk
```
```powershell
docker compose up -d
```
If this fails with `failed to connect to the docker API` — Docker Desktop isn't running; open the app, wait for the whale icon in the tray to go steady, then retry. `docker ps` (empty table, no error) confirms it's up.

### 6b. Deployed Kafka (Azure Container Instances, public IP)

**Critical:** `KAFKA_CONTROLLER_QUORUM_VOTERS` must use `localhost`, **not** the public FQDN — otherwise the broker tries to route its own internal controller traffic over the public internet to itself and crash-loops (confirmed cause of a `CrashLoopBackOff` / `restartCount` climbing into the hundreds in earlier testing).

```powershell
az provider register --namespace Microsoft.ContainerInstance

az container create `
  --resource-group lostfound-rg `
  --name lostfound-kafka `
  --image confluentinc/cp-kafka:7.5.0 `
  --os-type Linux `
  --ip-address Public `
  --dns-name-label lostfound-kafka `
  --ports 9092 `
  --cpu 1 `
  --memory 1.5 `
  --environment-variables `
    KAFKA_NODE_ID=1 `
    KAFKA_PROCESS_ROLES=broker,controller `
    KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093 `
    KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://lostfound-kafka.southeastasia.azurecontainer.io:9092 `
    KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT `
    KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER `
    KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 `
    KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 `
    KAFKA_AUTO_CREATE_TOPICS_ENABLE=true `
    CLUSTER_ID=MkU3OEVBNTcwNTJENDM2Qk
```
**`--os-type Linux` is required explicitly** — omitting it can cause `(InvalidOsType)` errors, Azure doesn't always infer it correctly from the image.

If creation fails with `RegistryErrorResponse ... index.docker.io` — Docker Hub's anonymous pull rate limit (100 pulls/6hrs per source IP, often exhausted by other tenants sharing Azure's regional IP pool, not by your own usage). This is a sliding limit, not a ban — just retry after a short wait; no account or payment needed to resolve it.

### Verify Kafka is healthy
```powershell
az container show --resource-group lostfound-rg --name lostfound-kafka --query "containers[0].instanceView.{state:currentState.state, restarts:restartCount}" -o table
docker run --rm confluentinc/cp-kafka:7.5.0 kafka-topics --list --bootstrap-server lostfound-kafka.southeastasia.azurecontainer.io:9092
```
Should show `Running`, `restarts: 0`, and the topics command returns cleanly (empty list is fine — just shouldn't hang or time out).

### Watching real events flow (for debugging Kafka-consuming code)
```powershell
docker run --rm confluentinc/cp-kafka:7.5.0 kafka-console-consumer --topic <topic-name> --bootstrap-server lostfound-kafka.southeastasia.azurecontainer.io:9092 --from-beginning
```
`--from-beginning` replays every message ever published to that topic, not just new ones — useful for confirming historical event shape without re-triggering the app.

### Known harmless log noise (confirmed, not a real problem)
The container's own log (`az container logs --resource-group lostfound-rg --name lostfound-kafka`) shows two categories of noise that look alarming but don't affect real functionality:
- **Recurring internal heartbeat timeouts** (`Unable to send a heartbeat because the RPC got timed out before it could be sent`, every 5–10 min, broker talking to its own controller on `localhost:9093`) — likely resource contention on a single-vCPU container running broker+controller simultaneously. Confirmed harmless by watching a live consumer during real app usage — actual events were delivered correctly with valid payloads throughout.
- **Garbage/malformed packets** (`InvalidReceiveException`, `Unexpected api key: -173`) from internal Azure IP ranges hitting the broker's public port — expected background noise for a `PLAINTEXT`, unauthenticated, publicly-exposed broker (likely Azure's own infrastructure probing), not application traffic.

If this pattern ever correlates with genuine message loss (not just log noise), the next step is bumping to 2 vCPU / 2GB — requires delete + recreate (ACI has no in-place resize):
```powershell
az container delete --resource-group lostfound-rg --name lostfound-kafka --yes
```
then re-run the create command above with `--cpu 2 --memory 2`.

### Cost & stopping to save money
Container Instances is **not free** on any Azure tier — this is the main ongoing cost of the project. App Service (F1) and MySQL (B1MS Burstable, within free-tier hours if applicable) may be free depending on account type; check **Cost Management + Billing → Cost analysis** in the Portal for real figures (`az consumption usage list` is unreliable/preview and often shows blank cost data).

```powershell
az container stop --resource-group lostfound-rg --name lostfound-kafka
az container start --resource-group lostfound-rg --name lostfound-kafka
```
**Stopping/starting the same container group (not delete + recreate) preserves its public IP and DNS label** — confirmed safe for this project's cost-saving routine. After any restart, double check the FQDN still matches what's in the App Service setting anyway:
```powershell
az container show --resource-group lostfound-rg --name lostfound-kafka --query ipAddress.fqdn -o tsv
az webapp config appsettings list --name lostfound-auth-service --resource-group lostfound-rg --query "[?name=='Kafka__BootstrapServers'].value" -o tsv
```

---

## 7. SMTP / Real Email Delivery

The project ships with **Mailtrap** placeholder values in `appsettings.Development.json` (`your-mailtrap-user` / `your-mailtrap-password`) — these are inert template text, never filled in, and Mailtrap's *Email Testing* product (what that host implies) is a local-dev sandbox that never delivers to real inboxes regardless. These values are physically incapable of affecting the deployed app: .NET only loads `appsettings.{Environment}.json`, and App Service defaults to `Production`, so `appsettings.Development.json` is never read there at all — no risk of cross-contamination between local and deployed config.

For real deployed delivery, use **SendGrid** (matches what `appsettings.Production.json` already expects). SendGrid's single-sender verification (verify one email you own) is faster to set up for a student project than Mailtrap's Sending product, which requires full DNS domain verification.

1. Sign up at sendgrid.com
2. **Settings → Sender Authentication → Verify a Single Sender**
3. **Settings → API Keys → Create API Key** → Restricted Access → Mail Send only → copy immediately (`SG.` prefix, shown once)
4. Push to App Service:
```powershell
az webapp config appsettings set --name lostfound-auth-service --resource-group lostfound-rg --settings Smtp__Host="smtp.sendgrid.net" Smtp__Port="587" Smtp__User="apikey" Smtp__Password="SG.<real-key>" Smtp__FromAddress="<verified-sender-email>" Smtp__FromName="Lost & Found"
```
**`Smtp__User` must be the literal string `apikey`** — not your SendGrid account email. SendGrid's SMTP relay always authenticates this way; the real secret goes entirely in `Smtp__Password`.

No code changes needed — `Smtp__Host` (flat env var) automatically un-flattens into the same nested `"Smtp": { "Host": ... }` shape used elsewhere, via the standard .NET `__` = nesting convention (same pattern as every other `X__Y` setting in this project). Setting App Service settings triggers an automatic restart — changes take effect within seconds.

### Verifying delivery actually happened
The email service is deliberately "fire-and-forget" — catches every send exception, logs a `LogWarning`, and returns success to the caller regardless. A successful API response or UI success message does **not** confirm the email arrived; a silent complete non-delivery produces no log line at all. The only reliable confirmation is **SendGrid's own Activity dashboard** — shows request count, delivered %, bounce %, spam-report %, independent of the app's own logs. If SendGrid shows "delivered" but nothing's visible, check spam/junk first — a brand-new sending identity with no reputation history is commonly filtered there on the first few sends.

---

## 8. Push App Service Settings (full list)

### CLI
```powershell
az webapp config appsettings set `
  --name lostfound-auth-service `
  --resource-group lostfound-rg `
  --settings `
    Kafka__BootstrapServers="lostfound-kafka.southeastasia.azurecontainer.io:9092" `
    APPLICATIONINSIGHTS_CONNECTION_STRING="<app-insights-connection-string>" `
    Jwt__Secret="<jwt-secret>" `
    ConnectionStrings__MySql="Server=lostfound-mysql.mysql.database.azure.com;Database=auth_db;Uid=<admin-username>;Pwd=<admin-password>;SslMode=Required;" `
    Smtp__Host="smtp.sendgrid.net" `
    Smtp__Port="587" `
    Smtp__User="apikey" `
    Smtp__Password="SG.<real-key>" `
    Smtp__FromAddress="<verified-sender-email>" `
    Smtp__FromName="Lost & Found" `
    Cors__AllowedOrigins__0="<deployed-frontend-url>"
```

### Portal (GUI)
App Service → **Configuration → Application settings** → **+ New application setting** per value above → **Save**.

**Note:** the double underscore (`__`) is how flat environment variables map onto nested `appsettings.json` sections (`Jwt:Secret`, `Smtp:Host`, etc.) — a .NET configuration convention, not a typo. `APPLICATIONINSIGHTS_CONNECTION_STRING` is the one exception (single underscore, special-cased by the App Insights SDK directly — see section 5).

### JWT Secret generation (one-time, shared across all 4 services)
```powershell
-join ((48..57) + (65..90) + (97..122) | Get-Random -Count 64 | ForEach-Object {[char]$_})
```

### Verify
```powershell
az webapp config appsettings list --name lostfound-auth-service --resource-group lostfound-rg --output table
```

---

## 9. GitHub Repo Setup

### CLI
```powershell
git init
git remote add origin https://github.com/<username>/lostfound-app.git
git branch -M main
git push -u origin main
git checkout -b develop
git push -u origin develop
```

### Portal (GUI)
1. Create the repo on github.com (empty)
2. Push as above
3. Repo → **Settings → Branches → Add branch ruleset** → target `main` → **"Require a pull request before merging"**
4. Confirm `develop` exists, intentionally left unprotected

`.gitignore`: `dotnet new gitignore` gives the base .NET rules; add a Node/React section on top for the frontend.

---

## 10. GitHub Actions — Backend (`auth-service-ci-cd.yml`)

```yaml
name: auth-service-ci-cd
on:
  push:
    branches: [develop, main, 'feature/**']
    paths: ['services/AuthService/**', 'services/AuthService.Tests/**']

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet restore services/AuthService
      - run: dotnet restore services/AuthService.Tests
      - run: dotnet build services/AuthService --no-restore
      - run: dotnet build services/AuthService.Tests --no-restore
      - run: dotnet test services/AuthService.Tests --no-build --collect:"XPlat Code Coverage"

  deploy:
    needs: build-and-test
    if: github.ref == 'refs/heads/develop'
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - run: dotnet publish services/AuthService -c Release -o services/AuthService/publish
      - uses: azure/webapps-deploy@v3
        with:
          app-name: 'lostfound-auth-service'
          publish-profile: ${{ secrets.AZURE_AUTH_SERVICE_PUBLISH_PROFILE }}
          package: services/AuthService/publish
```

**Key points:**
- `paths` covers **both** `AuthService/` (the app) and `AuthService.Tests/` (a sibling folder, not nested inside) — a filter on only the first means pushes touching just the test project never trigger the pipeline, and `dotnet test` pointed at the wrong folder silently finds and runs nothing
- Triggers on `develop`, `main`, and `feature/**` — real feedback on feature branches before merging, not just after
- `deploy` currently gated to `develop` — `main` is kept as a clean, reviewed archive; `build-and-test` still runs there via the top-level trigger, `deploy` just skips itself (shows as "skipped" in the Actions tab)
- `app-name` must exactly match the real Azure App Service name

### Creating the file (CLI, most reliable — avoids GitHub's suggested-template picker)
```powershell
mkdir .github
mkdir .github\workflows
notepad .github\workflows\auth-service-ci-cd.yml
```
Paste, save, then:
```powershell
git add .github/workflows/auth-service-ci-cd.yml
git commit -m "[devops] add auth-service ci/cd pipeline"
git push origin develop
```
**A change to the workflow file itself does not trigger a run** — the `paths` filter only watches `services/AuthService/**`, so a commit that only touches `.github/workflows/` needs a follow-up commit touching the actual service folder to fire the pipeline for the first time. Re-running an old failed job from the Actions tab also does **not** pick up a workflow file edit — GitHub evaluates using the workflow version as it existed at that push's original commit; only a fresh push evaluates the current file. Secret value changes (not the workflow file itself) *do* apply correctly on a re-run, since secrets are read live at run time, not baked into the commit.

### Publish Profile → GitHub Secret
```powershell
az webapp deployment list-publishing-profiles --name lostfound-auth-service --resource-group lostfound-rg --xml
```
Copy the full XML. Repo → **Settings → Secrets and variables → Actions → New repository secret** → name `AZURE_AUTH_SERVICE_PUBLISH_PROFILE` → paste.

**If deploy fails with `Publish profile is invalid for app-name and slot-name provided`:** confirm SCM Basic Auth is On (section 4), re-download a fresh profile, replace the secret, then **Re-run failed jobs** from the Actions tab (no new commit needed — secrets are read fresh on every run).

---

## 11. GitHub Actions — Frontend (`frontend-ci-cd.yml`, Azure Static Web Apps)

The frontend is a Vite + React SPA, deployed to **Azure Static Web Apps** (not App Service — that's for server-side runtimes only; static files need a different product). Setup here uses a manually-created deployment token rather than GitHub's OAuth-linked auto-setup, avoiding a second, differently-shaped CI/CD pattern being auto-generated into the repo.

### Create the Static Web App (no GitHub source linked at creation)
```powershell
az staticwebapp create --name lostfound-frontend --resource-group lostfound-rg --location southeastasia --sku Free
```

### Get the deployment token
```powershell
az staticwebapp secrets list --name lostfound-frontend --resource-group lostfound-rg --query "properties.apiKey" -o tsv
```
Add as GitHub secret `AZURE_STATIC_WEB_APPS_API_TOKEN`. To regenerate if needed:
```powershell
az staticwebapp secrets reset-api-key --name lostfound-frontend --resource-group lostfound-rg
```

### Workflow file
```yaml
name: frontend-ci-cd
on:
  push:
    branches: [develop, main, 'feature/**']
    paths: ['frontend/**']

jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '20'
      - run: npm ci
        working-directory: frontend
      - run: npm run build
        working-directory: frontend

  deploy:
    needs: build-and-test
    if: github.ref == 'refs/heads/develop'
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-node@v4
        with:
          node-version: '20'

      - run: npm ci
        working-directory: frontend

      - run: npm run build
        working-directory: frontend
        env:
          VITE_AUTH_API_BASE_URL: https://lostfound-auth-service.azurewebsites.net
          VITE_ITEM_API_BASE_URL: http://localhost:5001
          VITE_MATCHING_API_BASE_URL: http://localhost:5002
          VITE_ADMIN_API_BASE_URL: http://localhost:5003

      - name: List dist contents (debug)
        run: ls -la frontend/dist

      - name: Deploy
        uses: Azure/static-web-apps-deploy@v1
        with:
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
          repo_token: ${{ secrets.GITHUB_TOKEN }}
          action: "upload"
          app_location: "/frontend/dist"
          output_location: ""
          skip_app_build: true
```

### Why it's shaped this way (real issues hit, in order)

**`npm ci`/`npm run build` fails locally with `'tsc' is not recognized`** — dependencies were never installed (`node_modules` correctly gitignored, but `npm install` hadn't been run yet on that machine). Every fresh clone needs `npm install` once before the first build.

**The `Azure/static-web-apps-deploy@v1` action fails with a bare "An unknown exception has occurred"** — a known, common failure for that action; it swallows real errors from its own internal build container. Fix: don't let the action build anything itself — build explicitly first (the `npm ci` + `npm run build` steps in `deploy` above), then set `skip_app_build: true` so the action only uploads the already-built `dist/`. Note `skip_api_build` is **not** a valid input for this action version (produces a harmless "unexpected input" warning if included — omit it).

**If the "unknown exception" persists even with `skip_app_build: true`:** a stale/mismatched deployment token has been observed to cause this exact generic error — regenerate via `reset-api-key` above and update the GitHub secret.

**Blank white page, console shows `Failed to load module script ... MIME type "application/octet-stream"`:** two independent causes to check —
- `staticwebapp.config.json` must live in **`frontend/public/`** (Vite only copies `public/` contents into `dist/`) — a copy left in `frontend/` root does nothing. Since `Move-Item` on disk isn't a git operation on its own, moving the file needs an explicit `git add` on **both** the old and new paths staged together for git to record the move correctly (otherwise the old path can remain tracked on the remote even though it's gone locally).
  ```json
  {
    "navigationFallback": {
      "rewrite": "/index.html",
      "exclude": ["/assets/*", "/*.{css,js,ico,png,jpg,jpeg,svg,gif,webp,woff,woff2,ttf,json}"]
    },
    "mimeTypes": { ".js": "text/javascript", ".mjs": "text/javascript" }
  }
  ```
- **`app_location`/`output_location` combination matters.** `app_location: "/frontend"` + `output_location: "dist"` was found to sometimes serve the raw *source* `index.html` (which references `/src/main.tsx`, a dev-only path Vite's dev server handles specially — never valid in a static production deploy) instead of the *built* `dist/index.html` (which correctly references the compiled `/assets/index-<hash>.js`). Confirmed by comparing the two files directly, and by checking the Network tab for a request to `main.tsx` vs a hashed `assets/*.js` file. Fix: point `app_location` **directly** at the already-built folder (`"/frontend/dist"`) with `output_location` left empty, as shown above.

**App loads and immediately crashes** with `Uncaught Error: Missing required env var: VITE_ITEM_API_BASE_URL` (or similar) — the app does a hard startup check that every `VITE_*_API_BASE_URL` is present, even for services that don't exist in Azure yet. Fix: set placeholder values for every not-yet-built service alongside the one real value, as in the `env:` block above. **Vite bakes these in at build time**, not runtime — the workflow's own `env:` block is the actual source of truth for what ends up in the deployed build; the Static Web App resource's Portal-level "Environment variables" setting is a secondary/less reliable path for this reason.

The `ls -la frontend/dist` debug step is worth keeping permanently — cheap, and immediately shows whether expected files (including `staticwebapp.config.json`) actually made it into the build output on any future failure.

### Get the deployed URL
```powershell
az staticwebapp show --name lostfound-frontend --resource-group lostfound-rg --query defaultHostname -o tsv
```

### Set CORS on the backend to allow it
```powershell
az webapp config appsettings set --name lostfound-auth-service --resource-group lostfound-rg --settings Cors__AllowedOrigins__0="https://<hostname-from-above>"
```

---

## 12. Repeat per service in later sprints

Same pattern for Item Service (Sprint 2), Matching Service (Sprint 3), Admin Verify Service (Sprint 4):

1. Create the App Service (section 4) — remember to enable SCM Basic Auth immediately
2. Run that service's migrations manually against its own schema, matching the real DB name exactly (section 3)
3. Push its app settings — own MySQL schema, shared Kafka/App Insights/JWT, add `AddApplicationInsightsTelemetry()` in its own `Program.cs` (section 5, 8)
4. Add its own workflow file, path-filtered to its own service folder **and** its sibling `.Tests` folder (section 10)
5. Get its publish profile, add as a new GitHub secret, confirm SCM Basic Auth is On before the first deploy attempt
6. Update the frontend's placeholder `VITE_*_API_BASE_URL` to the real deployed URL once live
7. Verify: push to `develop` → check Actions tab → confirm `build-and-test` and `deploy` both pass → smoke test the real endpoint

Shared infrastructure (resource group, plan, MySQL server, Kafka container, App Insights, Static Web App) is created once and reused by every service.

---

## 13. End-to-end flow

```
Dev commits to feature/<epic>-service
    ↓
QA adds xUnit tests to the same branch
    ↓
merge feature/<epic>-service → develop   (direct push, no PR required)
    ↓  (build-and-test + deploy both run — develop is the active deploy target)
Live at https://lostfound-<service-name>.azurewebsites.net
    ↓  (once verified)
merge develop → main   (PR required, review needed)
    ↓  (build-and-test runs; deploy is skipped — main is a clean, reviewed archive)
```

---

## 14. Quick verification checklist (after any deploy)

```powershell
# App settings are correct
az webapp config appsettings list --name lostfound-auth-service --resource-group lostfound-rg --output table

# MySQL firewall is open
az mysql flexible-server firewall-rule list --resource-group lostfound-rg --name lostfound-mysql --output table

# Kafka container is healthy
az container show --resource-group lostfound-rg --name lostfound-kafka --query "containers[0].instanceView.{state:currentState.state, restarts:restartCount}" -o table

# Kafka FQDN matches what App Service is configured to use
az container show --resource-group lostfound-rg --name lostfound-kafka --query ipAddress.fqdn -o tsv
az webapp config appsettings list --name lostfound-auth-service --resource-group lostfound-rg --query "[?name=='Kafka__BootstrapServers'].value" -o tsv

# Real events are flowing (replace topic name as needed)
docker run --rm confluentinc/cp-kafka:7.5.0 kafka-console-consumer --topic auth.user.verified --bootstrap-server lostfound-kafka.southeastasia.azurecontainer.io:9092 --from-beginning
```
Then check GitHub Actions for a green run on both `auth-service-ci-cd` and `frontend-ci-cd`, and confirm App Insights Logs (`requests | order by timestamp desc | take 20`) shows real rows after triggering traffic. For email specifically, check SendGrid's own Activity dashboard rather than the app's logs — the app's fire-and-forget email service only logs failures, never successes.
