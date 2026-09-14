# Sprint 2 - Item Service Deployment Checklist

**Scope:** covers what is ALREADY deployed to Azure and verified live. The
`feature/Item-Service` merge (LF-66 resolve / LF-67 item details / LF-68 delete /
LF-69 My Reports `/mine` endpoints) is intentionally NOT included - it is local-only

---

## Sprint 2 background 

- [x] added the Item Service CI/CD workflow (`item-service-ci-cd.yml`)
- [x] Blob Storage setup documented for item images (Sprint 2 addition)
- [x] updated `Azure-DevOps-Setup-Reference.md`
- [x] added Item Service config files (`appsettings.json` / `appsettings.Development.json` / `appsettings.Production.json`)

**Main sprint goal - Item Service feature code**: report lost/found
items, edit/update item report, delete item reports, view my reports, search and browse items, mark an item as resolved, photo upload (local disk or Azure Blob), Kafka event publishing, JWT auth, xUnit +
Selenium + JMeter test coverage. This was the functional work the deployment below supports.

---

## Azure Infrastructure

- [x] Resource group (`lostfound-rg`) and App Service Plan (`lostfound-plan`, F1 Free) reused from Sprint 1
- [x] App Service created for Item Service (`lostfound-item-service`, Linux, .NET 8) - **reaches via** `https://lostfound-item-service-f9htesh2drbkd0dh.southeastasia-01.azurewebsites.net`
- [x] Blob Storage account `lostfoundblob8842` created with `lostfound-item-images` container (public-read) for item photos
- [x] MySQL Flexible Server `lostfound-mysql` reused; `item_db` schema used by Item Service (no new server)
- [x] Kafka broker reused (`lostfound-kafka`, Azure Container Instances, `confluentinc/cp-kafka:7.5.0`)
- [x] Application Insights reused (`lostfound-insights`)
- [x] Azure Static Web App reused for the frontend (`lostfound-frontend` -> `https://kind-coast-0034d9700.5.azurestaticapps.net`)

## Database (`item_db`)

- [x] Migrations V001-V004 run against Azure `item_db` via consolidated `DevOps_Documentation/azure-item_db-migration.sql`
- [x] Schema verified: `lost_items`, `lost_item_photos`, `found_items`, `found_item_photos` present with correct columns
- [x] **V005 + V006 applied** (manual run on `item_db`): `deleted_at DATETIME(3) NULL` + `ix_lost_items_deleted_at` / `ix_found_items_deleted_at`
  - Note: the *deployed* Item Service code does not reference `deleted_at` yet (it arrives with the LF-67/68 merge). Columns are present and forward-compatible; nothing on the live app queries them.
- [x] SSL/TLS (`SslMode=Required`) confirmed in the production connection string

## App Service Configuration (`lostfound-item-service`)

- [x] `ConnectionStrings__MySql` set (Azure MySQL `item_db`, SSL required)
- [x] `Jwt__Secret` set (shared secret matching Auth Service)
- [x] `Kafka__BootstrapServers` set to the live Kafka container FQDN
- [x] `APPLICATIONINSIGHTS_CONNECTION_STRING` set
- [x] `Cors__AllowedOrigins__0` set to the deployed frontend's origin
- [x] `BlobStorage__ConnectionString` set (storage account connection string)
- [x] `BlobStorage__ContainerName` set (`lostfound-item-images`)
- [x] `BlobStorage__PublicBaseUrl` set (`https://lostfoundblob8842.blob.core.windows.net/lostfound-item-images`)
- [x] SCM Basic Auth Publishing Credentials enabled (required for publish-profile deploys)

## CI/CD - Backend (`item-service-ci-cd.yml`)

- [x] Triggers on push to `develop`, `main`, and `feature/Item-Service`, path-filtered to `services/ItemService/**` and `services/ItemService.Tests/**`
- [x] `build-and-test` runs `dotnet restore` / `build` / `test` with `--collect:"XPlat Code Coverage"` on every matching push
- [x] `deploy` job gated to `develop` only (`if: github.ref == 'refs/heads/develop'`)
- [x] Publish profile stored as `AZURE_ITEM_SERVICE_PUBLISH_PROFILE` GitHub secret
- [x] Deploys via `azure/webapps-deploy@v3` to `lostfound-item-service`

## CI/CD - Frontend (`frontend-ci-cd.yml`)

- [x] `VITE_ITEM_API_BASE_URL` set to the real Item Service URL
- [x] `VITE_AUTH_API_BASE_URL` set to the live Auth Service URL
- [x] Matching (`VITE_MATCHING_API_BASE_URL`) / Admin (`VITE_ADMIN_API_BASE_URL`) still placeholder `localhost` values - services not yet built
- [x] Builds explicitly (`npm ci` + `npm run build`), deploys pre-built `frontend/dist` with `skip_app_build: true`

## Cross-Service Authentication Fix (Sprint 2, DevOps)

- [x] Auth Service now returns the JWT in login / verify / reset / me response bodies
- [x] Frontend sends `Authorization: Bearer <jwt>` to non-auth services (Item, etc.) using an in-memory token, so Item Service can validate the caller
- [x] Live result: browse/search on the deployed Item Service returns **200 with a valid Bearer token**, **401 without** (auth is enforced)

## Verified via Live API 

- [x] Frontend loads at the static web app URL (HTTP 200)
- [x] Login works against live Auth Service - returns a JWT in the response body
- [x] `GET /api/items?q=smoke` with Bearer token -> **200**, returns search results
- [x] `GET /api/items?q=smoke` **without** token -> **401** (auth is enforced)
- [x] `GET /api/items/lost/mine` / `/found/mine` -> **404** (expected - `/mine` endpoints ship with the LF-69 merge, not yet deployed)
- [x] `/swagger` and `/` on the Item Service -> **404** (expected - Swagger is intentionally disabled in Production; root has no content)

## Live Smoke Tests to Complete

These are implemented + CI-tested, but not yet manually exercised against the live
deployment.

- [x] Report a Lost Item end-to-end (wizard + photo)
- [x] Report a Found Item end-to-end (wizard + photo)
- [ ] Edit/update a report
- [x] Browse/search with filters and pagination from the UI
- [x] Confirm Item Service `items.*` Kafka topics on the broker.

## Known / Accepted Non-Blockers

- Item Service **report/edit/photo** flows are implemented and CI-tested but not yet
  manually smoke-tested against the live deployment (flagged above).
- `feature/Item-Service` merge (LF-66/67/68/69 + V005-V006 *code*) is **local-only**
  (`develop` is ahead of `origin/develop`); not deployed in this sprint by design.
- Kafka ACI is commonly stopped to save cost (`az container stop`); it bills
  continuously while running. Confirm broker state before event-dependent smoke tests.
- Matching Service and Admin Verify Service are not yet built (Sprints 3-4); the
  frontend's `VITE_MATCHING_API_BASE_URL` / `VITE_ADMIN_API_BASE_URL` are placeholder
  `localhost` values, and Kafka `items.*.resolved` / `*.delete_requested` events have no consumer yet - expected, not a bug.
- Swagger is intentionally disabled in Production (`Program.cs` gates it to Development) - a 404 on `/swagger` is correct.