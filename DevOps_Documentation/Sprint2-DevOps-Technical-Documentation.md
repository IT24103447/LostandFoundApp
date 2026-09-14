# Lost & Found App - Sprint 2 DevOps Technical Documentation

**Service:** Item Service
**Sprint:** 2
**Status:** Deployed, verified end-to-end (live API, UI smoke tests, all 7 Kafka topics confirmed).

---

## 1. Architecture Overview

### 1.1 System design
Microservice architecture, 4 independently deployable services (Auth, Item, Matching, Admin Verify - Item built by Sprint 2), each with its own MySQL schema. No API gateway - the frontend calls each service's App Service URL directly over REST. Services communicate with each other asynchronously via a shared, self-hosted Kafka broker, never via direct synchronous calls to one another. Item Service adds one infrastructure piece Auth Service never needed: **Azure Blob Storage** for uploaded item photos.

Key architectural facts established during Sprint 2:
- **One-way event flow.** Item Service **produces** its own events (all 7 `items.*` topics confirmed publishing on the live broker), and **nothing in the repo consumes any event yet** - Matching Service (Sprint 3) is the intended consumer.
- **How Item Service authenticates users:** it does NOT consume Auth events. It validates the caller's JWT synchronously via `JwtBearer` middleware using the shared secret, and reads `UserId` from the token's `sub` claim. The cross-service Bearer flow (section 6) makes this work.

### 1.2 Technology choices retained from Sprint 1
Identical stack: Azure App Service (Linux, F1 Free), Azure Database for MySQL Flexible Server (one schema per service), self-hosted Apache Kafka on Azure Container Instances (the only containerized piece), Azure Static Web Apps for the frontend, GitHub Actions with one path-filtered workflow per service, JWT with a shared secret across all services, and raw ADO.NET (parameterized SQL, no ORM).

### 1.3 Deployment topology
```
                    ┌─────────────────────────┐
                    │   Azure Static Web App    │
                    │  (React + Vite frontend)  │
                    └──────┬───────┬────────────┘
                           │       │ REST (direct, no gateway)
                           ▼       ▼
                    ┌─────────────┐     ┌─────────────────────────┐
                    │ Auth Service │     │   Item Service           │
                    │ (App Service)│     │   (App Service)          │
                    └───┬─────────┘     └───┬───────┬───────┬──────┘
                        │                   │       │       │
               ┌────────▼──┐       ┌────────▼──┐ ┌───▼──────▼───┐
               │ MySQL      │       │ MySQL      │ │ Kafka (ACI) │— topics:
               │ (auth_db)  │       │ (item_db)  │ │ self-hosted │  items.*.created
               └────────────┘       └─┬──────────┘ └───┬────────┘  items.*.updated
                                      │                │
                              ┌───────▼───────┐   ┌────▼───────────────┐
                              │ Blob Storage   │   │ Application       │
                              │ lostfoundblob8842│ │ Insights          │
                              │ (item images)  │   │ (shared Service    │
                              └───────────────┘   │  entry point)      │
                                                  └────────────────────┘
```

---

## 2. Infrastructure Inventory

| Resource | Name | Type | Tier/SKU | Cost status |
|---|---|---|---|---|
| Resource group | `lostfound-rg` | Container for all resources | - | Free (no direct cost) |
| App Service Plan | `lostfound-plan` | Compute for all App Services | F1 (Free) | Free |
| App Service | `lostfound-auth-service` | Auth Service hosting | Linux, .NET 8 | Free (on F1) - reused from Sprint 1 |
| App Service | `lostfound-item-service` | Item Service hosting | Linux, .NET 8 | Free (on F1) - **created Sprint 2** |
| MySQL Server | `lostfound-mysql` | Database | Burstable B1MS | Free within free-tier hours (account-dependent) |
| MySQL Schemas | `auth_db`, `item_db`, `matching_db`, `admin_verify_db` | Logical databases | - | No separate cost |
| Blob Storage | `lostfoundblob8842` | Item image storage (container `lostfound-item-images`) | Standard GPv2 (LRS) | Usage-based, negligible at this volume - **created Sprint 2** |
| Container Instance | `lostfound-kafka` | Self-hosted Kafka broker | 1 vCPU / 1.5GB | **Billed continuously** - the project's primary ongoing cost |
| Application Insights | `lostfound-insights` | Telemetry/observability | Web application type | Free tier allowance applies |
| Static Web App | `lostfound-frontend` | Frontend hosting | Free | Free |

**Cost note:** unchanged from Sprint 1 - Container Instances bills continuously (check **Cost Management + Billing → Cost analysis** for real figures; `az consumption usage list` is unreliable/preview). Blob Storage is the one new cost item; at student-project volume its per-GB storage and operations charges are effectively negligible and cannot be stopped (do not delete the account - Item Service's Production config throws without the connection string, see section 4).

---

## 3. Database Layer

### 3.1 Schema (`item_db`)
Matching Sprint 1's pattern: one schema per service, provisioned as a logical database on the shared `lostfound-mysql` server. Tables created from the consolidated V001-V004 script:

| Table | Purpose | Key columns |
|---|---|---|
| `lost_items` | Lost item reports | `id` (CHAR 36, UUID), `user_id`, `title`, `category`, `description`, `date_lost`, `last_known_location`, `hidden_information`, `status`, `created_at`, `updated_at` |
| `lost_item_photos` | Photos on a lost item | `id`, `lost_item_id` (FK, cascade), `url`, `created_at` |
| `found_items` | Found item reports | `id`, `user_id`, `title`, `category`, `description`, `date_found`, `location_found`, `hidden_information`, `status`, `created_at`, `updated_at` |
| `found_item_photos` | Photos on a found item | `id`, `found_item_id` (FK, cascade), `url`, `created_at` |

Indexes: `user_id`, `status`, and the date column on each item table (`ix_*_user_id`, `ix_*_status`, `ix_*_date_lost` / `ix_*_date_found`).

### 3.2 Migration process
Identical gating to Sprint 1: migration/seed logic in `Program.cs` runs **only under `Development`** (`if (app.Environment.IsDevelopment())` / `DbInitializer.RunPendingMigrations`), so **nothing auto-runs on Azure** (App Service defaults to `ASPNETCORE_ENVIRONMENT=Production`). Migrations are applied manually via the `mysql` CLI against the real provisioned schema.

- **V001-V004** - consolidated into `DevOps_Documentation/azure-item_db-migration.sql` and run once against Azure `item_db`
- **V005 + V006** - `deleted_at DATETIME(3) NULL` plus `ix_lost_items_deleted_at` / `ix_found_items_deleted_at`, consolidated into `DevOps_Documentation/azure-item_db-migration-v005-v006.sql` and **applied manually to `item_db`**
  - **Ordering note:** V005/V006 were applied to Azure *before* the LF-67/68 merge deployed, eliminating the pending-schema deploy risk. The deployed code now queries `deleted_at`; soft-delete is verified live (deleted items return **404** on subsequent GET and vanish from `/mine`).

**Execution method:** direct `mysql` CLI (Azure Database for MySQL Flexible Server has no built-in Portal query editor):
```powershell
Get-Content DevOps_Documentation\azure-item_db-migration-v005-v006.sql |
  mysql -h lostfound-mysql.mysql.database.azure.com -u <admin> -p item_db
```

### 3.3 Network security
Same posture as Sprint 1: `SslMode=Required` enforced in the production connection string, `AllowAzureServices` firewall rule (`0.0.0.0-0.0.0.0`, allows App Service regardless of its outbound IP) plus IP-pinned rules for local dev (re-add when the residential IP changes).

---

## 4. Blob Storage for Item Images (Sprint 2 addition)

Item Service stores uploaded photos in Azure Blob Storage - the one Azure primitive Auth Service never needed.

### 4.1 Account & container
- Storage account: `lostfoundblob8842`; container: `lostfound-item-images`
- Access level: `container` (public-read) 
- Auth method: storage account **connection string** (`az storage account show-connection-string --name lostfoundblob8842 --resource-group lostfound-rg -o tsv`)

### 4.2 Configuration
| Setting | Purpose |
|---|---|
| `BlobStorage__ConnectionString` | Storage account connection string |
| `BlobStorage__ContainerName` | `lostfound-item-images` |
| `BlobStorage__PublicBaseUrl` | `https://lostfoundblob8842.blob.core.windows.net/lostfound-item-images` - used to build each image's stable public URL |

Upload flow: service uploads the image and stores `{PublicBaseUrl}/{blobName}` in the DB / Kafka events; the frontend renders that URL directly. No SAS expiration handling needed.

### 4.3 Production guard (fail-fast, not fallback)
`Program.cs` deliberately **throws at startup** in Production if `BlobStorage:ConnectionString` is missing/blank, rather than silently falling back to local disk - local disk does not persist across restarts/redeploys and is not shared across instances:
```csharp
if (string.IsNullOrWhiteSpace(blobConnectionString) && builder.Environment.IsProduction())
    throw new InvalidOperationException("BlobStorage:ConnectionString is required in Production. ...");
```
In Development (or any environment where the setting is absent) it falls back to `LocalPhotoStorageService` writing under `wwwroot/photos`. This is why the account must never be deleted for cost reasons.

---

## 5. Event Streaming (Kafka)

Broker configuration is unchanged from Sprint 1 

### 5.1 Topic naming & the prefix fix
Topics are `{TopicPrefix}.{eventType}`. Early in Sprint 2 the prefix was inconsistent (`"item"`), fixed to **`"items"`** in commit `7874b8c` across `KafkaSettings.cs` and `appsettings.Production.json`. Naming quirk to be aware of: the delete topic is `items.item.delete_requested` while the rest are `items.lost_item.*` / `items.found_item.*`.

### 5.2 Events ITEM SERVICE PRODUCES

All 7 topics below were confirmed producing on the live broker (per-topic `kafka-console-consumer --from-beginning` verification, section 9.3):

| Topic | Event class | Triggered by | Key payload |
|---|---|---|---|
| `items.lost_item.created` | `LostItemCreatedEvent` | `POST /api/items/lost` | Full lost item: `LostItemId`, `Title`, `Category`, `Description`, `DateLost`, `LastKnownLocation`, `HiddenInformation`, `Status`, `PhotoUrls`, `CreatedAt` |
| `items.lost_item.updated` | `LostItemUpdatedEvent` | `PUT /api/items/lost/{id}` | Full lost item + `UpdatedAt` |
| `items.lost_item.resolved` | `LostItemResolvedEvent` | Resolve lost item | Full state incl. `ResolvedAt` |
| `items.found_item.created` | `FoundItemCreatedEvent` | `POST /api/items/found` | Full found item incl. `HiddenInformation`, `LocationFound`, `PhotoUrls`, `CreatedAt` |
| `items.found_item.updated` | `FoundItemUpdatedEvent` | `PUT /api/items/found/{id}` | Full found item + `UpdatedAt` |
| `items.found_item.resolved` | `FoundItemResolvedEvent` | Resolve found item | Full state incl. `ResolvedAt` |
| `items.item.delete_requested` | `ItemDeleteRequestedEvent` | Delete lost **or** found report (published from both controllers) | `ItemId`, `ItemType` ("LOST"/"FOUND") only |

All create/update/resolve events carry `HiddenInformation` and `PhotoUrls`; delete carries only the identity. Every event inherits `BaseEvent`: `EventId` (fresh GUID), `EventType`, `Timestamp` (UTC), `UserId` (the reporter). Serialized as compact **camelCase JSON**.

### 5.3 What ITEM SERVICE CONSUMES: **none**
Item Service does not consume any Kafka events - from Auth Service or anywhere else

How Item Service learns about users is therefore **synchronous, not event-driven**: it validates the caller's JWT per-request (shared secret) and takes `UserId` from the `sub` claim (section 6). Do not assume an `auth.*` consumer exists - the design is one-way event flow today.

### 5.5 Security note (carried risk)
Create/update/resolve events carry `HiddenInformation` (the secret-verification detail of a report) across a **`PLAINTEXT`, unauthenticated, publicly-exposed broker**. This is deliberate design for the future Matching Service consumer (which is privileged to see hidden info), but anyone who can reach the broker (any host on any port it's exposed on) can read these events. Same accepted risk as Sprint 1 - re-validated, not changed. The Matching/Admin services do not exist yet, so there is no cross-service Kafka traffic at all today.



---

## 6. Cross-Service Authentication (Sprint 2 fix)

### 6.1 Why it was needed
Item Service authenticates callers with `JwtBearer` (shared secret, `Program.cs:67-106`), reading `UserId` from `sub`. But until this fix the deployed frontend never sent a token to it: the frontend kept the JWT in `localStorage` and attached it only to Auth Service calls via cookies/`credentials`. Item Service's `[Authorize]` endpoints therefore rejected every real user request with 401.

### 6.2 The fix (two coordinated parts)
1. **Auth Service returns the JWT in response bodies.** Login, verify, reset, and `/me` responses now include a `token` field (`LoginResponse` / `UserProfileDto`), so the frontend can capture the JWT into memory without reaching into storage it should own.
2. **Frontend sends `Authorization: Bearer <jwt>` to non-auth services.** `apiClient` now attaches the in-memory Bearer header for every non-Auth API call; `AuthContext` holds the token in memory for the session and restores it on refresh. Cookie-based `credentials` remains the mechanism for Auth Service itself.

### 6.3 Live verification
- `GET /api/items?q=smoke` with a valid Bearer token -> **200** (search results returned)
- Same endpoint **without** a token -> **401** 
- Login against the live Auth Service returns a JWT in the response body (verified this session)
- The new merged endpoints also return **200** with a Bearer token: item details (`GET /api/items/lost|found/{id}`), `/mine`, resolve, and delete - confirming auth holds across the whole LF-66..69 surface (section 9).

**Design note:** this is a synchronous auth mechanism - Item Service and Auth Service have no `auth.*` consumer relationship (section 5.3). "Cross-service auth" here means *JWT validation*, not *event consumption*.

---

## 7. Observability

Application Insights wiring for Item Service mirrors the Sprint 1 lessons, applied via commit:
- `builder.Services.AddApplicationInsightsTelemetry()` must be **explicitly called** in `Program.cs` before `builder.Build()` - the connection string alone is not sufficient and produces silently-zero telemetry otherwise.
- Env var is special-cased: exactly `APPLICATIONINSIGHTS_CONNECTION_STRING` (all caps, single underscore) - it bypasses the `__` nested-config convention
- Shared App Insights resource `lostfound-insights` is reused; do not create per-service resources
- Verification pattern: trigger real traffic, then query Logs (`requests | order by timestamp desc | take 20`) - confirming real rows rather than implying they flow

Automatically captured (same as Auth): requests, MySQL dependencies, blob storage calls, Kafka producer calls, unhandled exceptions, and `ILogger` output (including the "buffer full / dropping event" warnings from section 5.4) as traces.

---

## 8. CI/CD Pipeline Design

### 8.1 Branch strategy
Unchanged from Sprint 1: `feature/<epic>-service` (active dev, CI on every push) → `develop` (active deploy target, no PR) → `main` (reviewed archive, deploy skipped). `develop` is merged into `main` at sprint close.

### 8.2 Backend pipeline (`item-service-ci-cd.yml`) - Sprint 2 addition
- Path filter covers **both** `ItemService/` and its sibling `ItemService.Tests/` (Same as Auth - filtering only the app folder never triggers on test-only changes)
- Triggers on `develop`, `main`, and `feature/Item-Service`; `deploy` is gated to `develop` only
- publish profile stored as `AZURE_ITEM_SERVICE_PUBLISH_PROFILE`
- SCM Basic Auth Publishing Credentials must be enabled on the App Service.

### 8.3 Frontend pipeline (`frontend-ci-cd.yml`)
Built on the concrete lessons recorded in Sprint 1:
- Builds explicitly (`npm ci` + `npm run build`) and uploads the pre-built `frontend/dist` with `skip_app_build: true`, `app_location: /frontend/dist`, empty `output_location` (avoids the deploy action's internal-build "unknown exception" and the raw-source `index.html` serving bug)
- `staticwebapp.config.json` lives in `frontend/public/` so Vite copies it into `dist/` (SPA navigation fallback + JS MIME types)
- `VITE_ITEM_API_BASE_URL` set to the real deployed Item Service URL
- `VITE_AUTH_API_BASE_URL` = live Auth Service; `VITE_MATCHING_API_BASE_URL` / `VITE_ADMIN_API_BASE_URL` remain `localhost:5002` / `localhost:5003` placeholders (services not built) - the app has a hard startup check for all four, so placeholders are required to keep it from crashing
- Vite bakes env at build time; the workflow `env:` block is authoritative, Portal-level SWA env vars are secondary
- The `ls -la frontend/dist` debug step is retained deliberately

### 8.4 Publish credentials 
Newer App Services have SCM Basic Auth disabled by default, breaking publish-profile deploys with `Publish profile is invalid for app-name and slot-name provided`. Must be On for `lostfound-item-service` too (Configuration → General settings → Platform settings).

---

## 9. Functional Verification Record

### 9.1 Live API verification (performed this session)
Direct HTTP checks against the deployed Azure endpoints 

| Check | Command (abbrev.) | Result |
|---|---|---|
| Frontend loads | `GET` on SWA URL | **200** |
| Login issues JWT in body | `POST /api/auth/login` (user1@example.com) | **200**, `token` present |
| Browse/search with auth | `GET /api/items?q=smoke` + Bearer | **200**, results returned |
| Browse/search without auth | `GET /api/items?q=smoke`, no header | **401** - auth enforced |
| `/mine` endpoints | `GET /api/items/lost/mine` / `/found/mine` + Bearer | **200** - LF-69 live after merge |
| Item details | `GET /api/items/lost|found/{id}` + Bearer | **200** - LF-67 live after merge |
| Resolve | `POST /api/items/lost|found/{id}/resolve` + Bearer | **200** (owner, ACTIVE); second call **409**; non-reporter **403**; unknown id **404**; no token **401** - LF-66 |
| Delete | `DELETE /api/items/lost|found/{id}` + Bearer | **200**; subsequent GET by id **404** + absent from `/mine` (soft-delete); non-reporter **403** - LF-68 |
| Swagger | `GET /swagger/index.html` | **404** - expected: disabled in Production |
| Root path | `GET /` | **404** - expected: no content |

### 9.2 UI smoke tests (manual, all passed)
- Report a Lost Item end-to-end (wizard + photo) - appears in My Reports
- Report a Found Item end-to-end (wizard + photo) - appears in My Reports
- Edit/update a lost and a found report from My Reports - saves correctly
- Mark reports as resolved from My Reports - status flips to RESOLVED
- Delete reports from My Reports - removed, soft-deleted server-side
- Homepage browse: search box and category/date filters return expected subsets

### 9.3 Kafka event verification (all topics confirmed)
One-shot capture per topic with `kafka-console-consumer --topic <t> --from-beginning --bootstrap-server lostfound-kafka.southeastasia.azurecontainer.io:9092` while exercising report/edit/resolve/delete live. Confirmed messages on all 7 topics - `items.lost_item.created` / `.updated` / `.resolved`, `items.found_item.created` / `.updated` / `.resolved`, `items.item.delete_requested` - with payloads matching the section 5.2 event shapes.



---

## 10. Known Limitations & Deliberate Deferrals

- **Details-page primary CTA is a deferred placeholder.** "I Found This Item" / "This Is My Item" on the item details page has no handler (`ItemDetailsPage.tsx` TODO) - the ownership-verification / matching conversation is a Sprint-3 Matching Service story. Item reports already capture `HiddenInformation` at create time, so the data needed for verification exists; only the participation/verification flow is missing. This is the one non-functional control in the shipped UI.
- **`MATCHED` status can't be produced live.** Delete of a `MATCHED` item returns 409 by code, and resolve requires `ACTIVE`; both guarded branches are unit-tested but not exercisable end-to-end until Matching Service (Sprint 3) can create matches.
- **No Kafka consumers exist yet.** All `items.*` topics are *verified publishing* (section 9.3) but have zero subscribers; Matching Service (Sprint 3) is the intended consumer of `.created`/`.updated`/`.resolved`/`.delete_requested`. Admin/Matching services contain no Kafka code yet.
- **Kafka broker may be stopped** (cost-saving `az container stop` is standard practice - it's the only continuously-billed resource). Check `az container show` before any event-dependent test; start/stop of the *same* group preserves the public IP and FQDN.
- **Blob container is public (`container` level)** - includes blob listing, broader than strictly needed; deliberate for the free static-URL pattern, no sensitive data (section 4.1). Tightening requires SAS + a code change (deferred).
- **`HiddenInformation` crosses a public plaintext broker** in create/update/resolve events - accepted design risk for the future privileged Matching consumer (section 5.5).
- **Matching Service and Admin Verify Service not yet built** (Sprints 3-4); their frontend base URLs are placeholder `localhost` values ported from Sprint 1 and unchanged.
- **Swagger intentionally disabled in Production** (`Program.cs` gates `UseSwagger`/`UseSwaggerUI` to Development) - a 404 on `/swagger` is correct behavior.
- **Kafka message ordering is not guaranteed** (random keys, no consumer anyway) - future consumers must not rely on per-item event sequence.

---

## 11. AI Assistant Usage

AI tools were used throughout this sprint to support research, implementation guidance, and infrastructure work around the Item Service.

Specific areas where AI assistance was used:
- Deploying/configuring the Item Service (App Service, Blob Storage, consolidated `item_db` migrations, Kafka topic-prefix fix)
- Designing and debugging the cross-service Bearer authentication fix (JWT in response bodies + in-memory Bearer header) and verifying it live
- Reviewing and fixing CI/CD pipelines: `item-service-ci-cd.yml` creation, and the Application Insights telemetry addition
- Auditing the Kafka event surface (produced events, topic naming)
- Drafting and organizing this documentation set.

## 12. Reference Documents

- `Sprint2-Deployment-Checklist.md` - condensed checklist of every setup and verification item for this sprint, quick reference
- `Sprint1-DevOps-Technical-Documentation.md` - Sprint 1 baseline: Auth Service, shared broker/MySQL/App Insights setup, Kafka noise baseline, branch strategy
- `Sprint1-Deployment-Checklist.md` - Sprint 1 checklist (Auth Service)
- `Azure-DevOps-Setup-Reference.md` - full step-by-step infra setup (CLI + Portal); includes the Blob Storage section (12b) added in Sprint 2
- `azure-item_db-migration.sql` - consolidated V001-V004 for Azure `item_db`
- `azure-item_db-migration-v005-v006.sql` - consolidated `deleted_at` migration for Azure `item_db` (idempotent, re-runnable)
- `Secrets-Map-AI-Safe-Reference.md` - credential inventory safe to share with AI assistants (no real values).