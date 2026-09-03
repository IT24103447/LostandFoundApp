# Lost & Found App — Secrets & Credentials Map (AI-Safe Reference)

> **Note to whoever uploads this file:** this document contains **no real secret values** — only names, purposes, and retrieval steps. It's safe to share with an AI assistant (Claude, ChatGPT, etc.) for troubleshooting help. Do **not** additionally upload the companion file `Secrets-and-Credentials-Reference.md` (the one with actual filled-in values) to any AI — that one is for human team members only.

---

## Prompt for the AI assistant (read this first)

> You are looking at a map of every secret/credential used in the Lost & Found App project — an ASP.NET microservices project deployed on Azure (App Service, MySQL Flexible Server, Container Instances running Kafka, Application Insights) with a React/Vite frontend on Azure Static Web Apps, CI/CD via GitHub Actions.
>
> This file intentionally contains **no real values** — only credential names, what each is for, and how a team member would retrieve or regenerate it. Use this file to:
> - Understand what infrastructure and third-party services this project depends on
> - Help the user figure out *which* credential is likely the cause of an issue they're describing (e.g. "email isn't sending" → point them at the SMTP section; "deploy is failing" → point them at the publish profile / SCM Basic Auth)
> - Give them the exact `az` CLI command or Portal path to retrieve or regenerate a given credential
> - Never ask the user to paste an actual secret value into the chat, and never suggest storing real values inside this file or committing them to source control
>
> If the user pastes an actual real value (an API key, connection string, password, etc.) into the conversation by mistake, flag it and suggest they treat it as compromised — rotate/regenerate it — since it's now been sent to a third-party service (the AI provider).

---

## GitHub Repository Secrets

| Name | What it's for | How to get/regenerate it | AI Instruction |
|---|---|---|---|
| `AZURE_AUTH_SERVICE_PUBLISH_PROFILE` | Lets GitHub Actions deploy AuthService to Azure App Service | Azure Portal → App Service → Overview → **Download publish profile**. Or CLI: `az webapp deployment list-publishing-profiles --name lostfound-auth-service --resource-group lostfound-rg --xml` | If deploy fails with "Publish profile is invalid for app-name and slot-name provided," first check whether **SCM Basic Auth Publishing Credentials** is enabled on the App Service (Configuration → General settings) — this is the most common cause, not a bad secret. Only suggest regenerating the profile after confirming that setting is On. |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Lets GitHub Actions deploy the frontend to Azure Static Web Apps | CLI: `az staticwebapp secrets list --name lostfound-auth-service --resource-group lostfound-rg --query "properties.apiKey" -o tsv`. Regenerate: `az staticwebapp secrets reset-api-key --name lostfound-auth-service --resource-group lostfound-rg` | If the deploy action fails with a vague "unknown exception," a stale/mismatched token is one known cause — suggest regenerating as a troubleshooting step, but check `app_location`/`output_location` config and `skip_app_build` settings first, since those cause the same generic error more often. |

---

## Azure App Service Application Settings

| Name | What it's for | How to get/regenerate it | AI Instruction |
|---|---|---|---|
| `ConnectionStrings__MySql` | Backend service's database connection | `Server=lostfound-mysql.mysql.database.azure.com;Database=auth_db;Uid=admin-username;Pwd=password;SslMode=Required;`. FQDN via `az mysql flexible-server show --name lostfound-mysql --resource-group lostfound-rg --query fullyQualifiedDomainName -o tsv` | `SslMode=Required` is mandatory — Flexible Server rejects unencrypted connections. If a DB connection error mentions SSL/TLS, check this first. If it's a timeout (not auth failure), suspect a firewall rule issue, not the connection string itself. |
| `Jwt__Secret` | Signs/validates JWTs — must be identical across all microservices | Generated once as a random string; no retrieval, only regeneration | If regenerating, warn the user this invalidates every currently-issued token (forces all logged-in users to re-authenticate) and must be updated identically across **every** service, not just the one being debugged. |
| `Kafka__BootstrapServers` | Address of the Kafka broker | `lostfound-kafka.southeastasia.azurecontainer.io:9092`. FQDN via `az container show --resource-group lostfound-rg --name lostfound-kafka --query ipAddress.fqdn -o tsv` | If a "brokers are down" error appears, first confirm the container is actually running (`az container show ... --query "containers[0].instanceView.currentState.state"`) before assuming this setting is wrong — ACI containers stopped for cost-saving are a common, non-bug cause. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Sends telemetry to Application Insights | Portal → App Insights resource → Overview → Connection String. Or CLI: `az monitor app-insights component show --app lostfound-insights --resource-group lostfound-rg --query connectionString -o tsv` | If telemetry isn't appearing even with this set correctly, the likely cause is a missing `builder.Services.AddApplicationInsightsTelemetry()` call in `Program.cs` — a code fix, not a config fix. Check the code before assuming the connection string is wrong. |
| `Smtp__Host` | SMTP relay address | Fixed value for the provider in use (e.g. SendGrid's is always `smtp.sendgrid.net`) | Not a secret — safe to state the fixed value directly if asked. |
| `Smtp__Port` | SMTP submission port | Fixed value (587 for authenticated submission) | Not a secret — safe to state directly. |
| `Smtp__User` | SMTP username | For SendGrid specifically: always the literal string `apikey`, never the account email | Flag this explicitly if the user seems to be using their account email instead — this is the single most common SMTP misconfiguration for this provider. |
| `Smtp__Password` | Real SMTP/API secret | Provider dashboard → API Keys → Create (shown once only) | Never ask the user to paste this. If they report auth failures, suggest they regenerate the key and re-set it, rather than trying to verify the existing value. |
| `Smtp__FromAddress` | Verified sender address | Provider dashboard → Sender Authentication | Must exactly match a verified sender in the provider's dashboard, or sends will be silently rejected server-side even with correct credentials — check this before assuming a code or auth problem. |
| `Smtp__FromName` | Display name shown to recipients | Free text, no verification needed | Not sensitive — safe to state or suggest directly. |
| `Cors__AllowedOrigins__0` | Which frontend origin may call this API | The deployed frontend's URL, prefixed with `https://` | If the frontend shows CORS errors in the browser console, this is almost always the cause — confirm it matches the frontend's *current* deployed URL exactly, no trailing slash. |

---

## Azure Static Web App Application Settings

| Name | What it's for | How to get/regenerate it | AI Instruction |
|---|---|---|---|
| `VITE_AUTH_API_BASE_URL` | Points the frontend at the live Auth Service | The App Service's default hostname (`https://lostfound-auth-service.azurewebsites.net`) | Not sensitive — a public URL. Safe to state directly. |
| `VITE_ITEM_API_BASE_URL` | Points the frontend at Item Service | Placeholder until that service is deployed (later sprint) | If the user reports errors calling Item Service features, confirm whether this is still a placeholder localhost value — expected to fail until that service actually exists in Azure. |
| `VITE_MATCHING_API_BASE_URL` | Points the frontend at Matching Service | Placeholder until that service is deployed (later sprint) | Same as above, for Matching Service. |
| `VITE_ADMIN_API_BASE_URL` | Points the frontend at Admin Verify Service | Placeholder until that service is deployed (later sprint) | Same as above, for Admin Verify Service. |

**Note:** these are Vite build-time variables — baked into the compiled output, not read at runtime. The actual source of truth is the `env:` block inside the frontend's GitHub Actions workflow file, not the Static Web App resource's Portal-level settings (which are a secondary/less reliable path for this reason).

---

## Azure MySQL Flexible Server

| Name | What it's for | How to get/regenerate it | AI Instruction |
|---|---|---|---|
| Admin username | Root-level DB access | `az mysql flexible-server show --name lostfound-mysql --resource-group lostfound-rg --query administratorLogin -o tsv` | Not sensitive on its own — safe to help retrieve. |
| Admin password | Paired with the above | Not retrievable if forgotten — must be reset via `az mysql flexible-server update --name lostfound-mysql --resource-group lostfound-rg --admin-password "new-password"` | Never ask the user to paste the current password. If it's forgotten, guide them to reset it rather than trying to recover it — Azure doesn't expose it anywhere. |

---

## Third-Party Service Accounts

| Service | What it's for | How to get/regenerate it | AI Instruction |
|---|---|---|---|
| Email delivery provider (e.g. SendGrid) account login | Dashboard access | Provider's own password reset flow if forgotten | Not something the AI needs to help retrieve directly — point the user to the provider's own account recovery. |
| Email delivery provider API key | Used as `Smtp__Password` | Provider dashboard → API Keys (shown once at creation) | If email isn't arriving, the AI should first suggest checking the provider's own delivery dashboard (e.g. SendGrid Activity) rather than assuming the key is wrong — many email codepaths are "fire-and-forget" and log nothing on real delivery failures at the receiving end. |

---

## Application Seed / Test Accounts

| Account | Purpose | How to reset if forgotten | AI Instruction |
|---|---|---|---|
| Seeded admin account | Initial testing/demo login | Reset via the app's own password-reset flow, or a direct DB update with a freshly generated password hash | If helping with a password reset, confirm the app's actual hashing algorithm (e.g. bcrypt) from the codebase before suggesting a raw SQL `UPDATE` — never assume a hash format without checking. |
| Seeded regular user account | Initial testing/demo login | Same as above | Same as above. |

---

## Docker Hub

No account/credentials currently used for this project — image pulls are anonymous public pulls. Not applicable unless this changes.

---

## Quick reference: where each credential type physically lives

| Credential type | Lives in |
|---|---|
| CI/CD secrets | GitHub repo Settings → Secrets and variables → Actions only |
| Backend app settings | Cloud host's App Service configuration only — never committed to source control |
| Frontend build-time settings | Deployment platform configuration **and** the CI/CD workflow file's env block |
| Local development placeholders | Committed to source control safely — these are non-functional template values, not real credentials |
| Database admin credentials | Known only to whoever provisioned the database server — not automatically stored anywhere else |
