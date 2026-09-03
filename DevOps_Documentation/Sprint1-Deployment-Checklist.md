# Sprint 1 - Auth Service Deployment Checklist (Complete)

## Azure Infrastructure

- [x] Resource group (`lostfound-rg`) and App Service Plan (F1 Free) created
- [x] Azure Database for MySQL Flexible Server created (`lostfound-mysql`), 4 schemas (`auth_db`, `item_db`, `matching_db`, `admin_verify_db`)
- [x] MySQL firewall rules configured (Azure services allowed + individual dev IPs as needed)
- [x] App Service created for Auth Service (`lostfound-auth-service`, Linux, .NET 8)
- [x] Kafka broker deployed as a self-hosted container (Azure Container Instances, KRaft mode, `confluentinc/cp-kafka:7.5.0`)
- [x] Application Insights resource created (`lostfound-insights`)
- [x] Azure Static Web App created for the frontend (`lostfound-frontend`)

## Database

- [x] All 7 migration scripts run against `auth_db` (consolidated into one combined script; V003/V004 bounce-tracking pair skipped as a net no-op)
- [x] Schema verified: `users`, `email_verification_tokens`, `password_reset_tokens` tables all present with correct columns
- [x] SSL/TLS (`SslMode=Required`) confirmed in the production connection string
- [x] Seed data inserted: 1 admin account, 1 regular user account (real bcrypt hashes, both pre-verified)

## App Service Configuration

- [x] `ConnectionStrings__MySql` set (Azure MySQL, SSL required)
- [x] `Jwt__Secret` set (shared secret for token signing/validation)
- [x] `Kafka__BootstrapServers` set, confirmed matching the live container's FQDN
- [x] `APPLICATIONINSIGHTS_CONNECTION_STRING` set
- [x] `Smtp__Host` / `Smtp__Port` / `Smtp__User` / `Smtp__Password` / `Smtp__FromAddress` / `Smtp__FromName` set (real SendGrid credentials, sender verified)
- [x] `Cors__AllowedOrigins__0` set to the deployed frontend's origin
- [x] SCM Basic Auth Publishing Credentials enabled (required for publish-profile deploys to work)

## CI/CD - Backend (`auth-service-ci-cd.yml`)

- [x] Triggers on push to `develop`, `main`, and `feature/**`, path-filtered to `services/AuthService/**` and `services/AuthService.Tests/**`
- [x] `build-and-test` job runs `dotnet restore` / `build` / `test` (xUnit) on every matching push
- [x] `deploy` job gated to `develop` only (temporary; `main` stays archive-only for now)
- [x] Publish profile stored correctly as `AZURE_AUTH_SERVICE_PUBLISH_PROFILE` GitHub secret

## CI/CD - Frontend (`frontend-ci-cd.yml`)

- [x] Triggers on push to `develop`, `main`, and `feature/**`, path-filtered to `frontend/**`
- [x] `build-and-test` job runs `npm ci` + `npm run build` on every matching push
- [x] `deploy` job builds explicitly (not relying on the Azure action's internal build) and uploads the pre-built `dist/` folder directly (`skip_app_build: true`, `app_location` pointed at `frontend/dist`)
- [x] `staticwebapp.config.json` in `frontend/public/` (correctly copied into `dist/` by Vite) fixes SPA routing + MIME-type handling for JS assets
- [x] Placeholder env vars set for not-yet-built services (`VITE_ITEM_API_BASE_URL`, `VITE_MATCHING_API_BASE_URL`, `VITE_ADMIN_API_BASE_URL`) so the app doesn't crash on startup validation
- [x] Real `VITE_AUTH_API_BASE_URL` set, pointing at the live Auth Service
- [x] Deployment token stored correctly as `AZURE_STATIC_WEB_APPS_API_TOKEN` GitHub secret

## Verified End-to-End (Smoke Test)

- [x] Frontend loads correctly at the deployed Static Web App URL
- [x] Admin login works (`admin1@lostandfound.com`) - JWT issued, admin dashboard renders
- [x] Registration works - new user created, verification email sent
- [x] Email delivery confirmed via SendGrid Activity dashboard (100% delivered, 0% bounced)
- [x] Kafka event flow confirmed working - `auth.user.profile_updated` and `auth.user.verified` events observed live with correct payloads and valid user IDs
- [x] CORS correctly configured - frontend can call the backend cross-origin with credentials
- [x] `develop` merged into `main`
- [x] Application Insights confirmed receiving live telemetry (`requests` table populated after wiring up `AddApplicationInsightsTelemetry()`)

## Verified - Full Auth Service Functional Flow (all user stories)

- [x] User registration - account created successfully with valid data
- [x] Email verification (OTP) - code sent, verified, `isEmailVerified` set correctly
- [x] Login - issues valid JWT, redirects correctly by role
- [x] Role-based access control - user vs admin routes correctly restricted
- [x] Profile update - changes save correctly, correct Kafka event published with updated values
- [x] Password reset - works end-to-end, new password takes effect on next login
- [x] Password hashing - confirmed via bcrypt, no plaintext storage
- [x] Admin: kick user - sets a reversible suspended flag (not a delete), kicked account correctly shows "suspended" on login attempt
- [x] Admin: unkick user - reverses the kick, account can log in normally again afterward
- [x] User: delete own account - hard-deletes the account (distinct from admin kick, which is a reversible soft flag)

## Known / Accepted Non-Blockers

- Kafka container occasionally logs internal heartbeat timeouts and background "brokers down" retries; does not appear to affect real event delivery (confirmed via live consumer test). Worth monitoring; a CPU/memory bump is a possible future fix if it worsens.
- Item Service, Matching Service, and Admin Verify Service are not yet built (Sprints 2–4) - their frontend base URLs are placeholder `localhost` values for now, calls to those features will fail until each service is deployed in its respective sprint.
- No `UserCreated`/`UserRegistered` Kafka event exists - intentional, since an unverified user can't perform any downstream actions anyway. `UserVerifiedEvent` is the actual "user now usable" signal, and it fires correctly.
