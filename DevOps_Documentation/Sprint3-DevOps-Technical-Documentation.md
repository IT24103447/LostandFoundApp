# Lost & Found App - Sprint 3 DevOps Technical Documentation

**Service:** Matching Service
**Sprint:** 3
**Status:** Deployed to Azure; full pipeline verified end-to-end live, including the notification/email subsystem (Section 6). This document supersedes `Sprint3-Matching-Service-Deployment-Checklist.md`, which was a mid-sprint snapshot, and **corrects an earlier draft of this document**, which concluded (based on the evidence available at the time) that the notification subsystem had never run in production. Section 6.2 explains what changed.

---

## 1. Architecture Overview

### 1.1 System design
Microservice architecture, 4 independently deployable services (Auth, Item, Matching, Admin Verify - Matching built by Sprint 3), each with its own MySQL schema. No API gateway - the frontend calls each service's App Service URL directly over REST. Services communicate with each other asynchronously via the shared, self-hosted Kafka broker, never via direct synchronous calls to one another.

Key architectural facts established this sprint:
- **Matching Service is a net consumer of Item Service's events**, plus one internal AI integration (Gemini) and its own outbox-driven producer. It consumes `items.*` events to build its own view of active reports, generates AI image descriptions for matching, runs its own matching/claim workflow over HTTP, and produces exactly **one** Kafka event type back out: `matches.confirmed` (Section 4.3).
- **Item Service closes the loop.** `matches.confirmed` is consumed by Item Service's `ConfirmedMatchConsumer`, which resolves both reports (`lost_item.resolved` / `found_item.resolved`) inside a single MySQL transaction guarded by an inbox table (`match_resolution_inbox`), so a redelivered Kafka message can never resolve the same match twice.
- **The outbox pattern is used on both sides of the `matches.confirmed` boundary.** Matching Service writes to `match_confirmation_outbox` and a background worker (`MatchConfirmationPublisher`) drains it to Kafka; this mirrors the pattern Item Service already used for its own `items.*` events in Sprint 2. Neither service ever publishes to Kafka synchronously inside a request.

### 1.2 Technology choices retained from Sprints 1-2
Identical stack: Azure App Service (Linux, F1 Free), Azure Database for MySQL Flexible Server (one schema per service), self-hosted Apache Kafka on Azure Container Instances, Azure Static Web Apps for the frontend, GitHub Actions with one path-filtered workflow per service, JWT with a shared secret across all services, raw ADO.NET (parameterized SQL, no ORM).

### 1.3 New for Sprint 3
- **Google Gemini** (`gemini-3.5-flash-lite`) for AI-generated image descriptions used in candidate matching (Section 5).
- **A second outbox/relay pair**, symmetric with Item Service's: `match_confirmation_outbox` → `MatchConfirmationPublisher` → `matches.confirmed`.
- **A notification subsystem** (`match_notifications` table, `NotificationDiscoveryService`, `NotificationRepository`, `NotificationDeliveryService`, `MatchEmailSender`) designed to email both parties at each stage of a match. Fully coded and unit-tested but **not started in production** - see Section 6.

### 1.4 Deployment topology
```
                    ┌─────────────────────────┐
                    │   Azure Static Web App    │
                    │  (React + Vite frontend)  │
                    └──────┬───────┬────────────┘
                           │       │ REST (direct, no gateway)
                           ▼       ▼
      ┌─────────────┐    ┌─────────────┐     ┌───────────────────┐
      │ Auth Service │    │ Item Service │     │ Matching Service   │
      │ (App Service)│    │ (App Service)│     │ (App Service)      │
      └───┬─────────┘    └───┬─────┬────┘     └──┬───────┬────────┘
          │                  │     │             │       │
   ┌──────▼──┐        ┌──────▼──┐  │      ┌──────▼──┐    │
   │ MySQL    │        │ MySQL    │  │      │ MySQL    │    │
   │(auth_db) │        │(item_db) │  │      │(matching_│    │
   └──────────┘        └──────────┘  │      │   db)    │    │
                                     │      └──────────┘    │
                          ┌──────────▼────────────┐   ┌─────▼─────┐
                          │  Kafka (ACI)           │   │  Gemini   │
                          │  self-hosted            │   │  API      │
                          │  items.*  →  Matching   │   └───────────┘
                          │  matches.confirmed → Item│
                          └────────────────────────┘
                                     │
                          ┌──────────▼────────────┐
                          │ Application Insights   │
                          │ (shared, all services)  │
                          └────────────────────────┘
```

---

## 2. Infrastructure Inventory

| Resource | Name | Type | Tier/SKU | Cost status |
|---|---|---|---|---|
| Resource group | `lostfound-rg` | Container for all resources | - | Free (no direct cost) |
| App Service Plan | `lostfound-plan` | Compute for all App Services | F1 (Free) | Free |
| App Service | `lostfound-auth-service` | Auth Service hosting | Linux, .NET 8 | Free (on F1) - reused |
| App Service | `lostfound-item-service` | Item Service hosting | Linux, .NET 8 | Free (on F1) - reused |
| App Service | `lostfound-matching-service` | Matching Service hosting | Linux, .NET 8 | Free (on F1) - **created Sprint 3** |
| MySQL Server | `lostfound-mysql` | Database | Burstable B1MS | Free within free-tier hours (account-dependent) - reused |
| MySQL Schema | `matching_db` | Matching Service logical database | - | No separate cost - **created Sprint 3** |
| Blob Storage | `lostfoundblob8842` | Item image storage (reused, read by Matching Service for Gemini input) | Standard GPv2 (LRS) | Usage-based, negligible - reused from Sprint 2 |
| Container Instance | `lostfound-kafka` | Self-hosted Kafka broker | 1 vCPU / 1.5GB | **Billed continuously** - reused |
| Application Insights | `lostfound-insights` | Telemetry/observability | Web application type | Free tier allowance - reused |
| Static Web App | `lostfound-frontend` | Frontend hosting | Free | Free - reused |
| External API | Google Gemini | AI image description generation | `gemini-3.5-flash-lite`, pay-per-call | Usage-based - **new Sprint 3** |

**Cost note:** unchanged pattern from Sprints 1-2 - Container Instances bills continuously (check **Cost Management + Billing → Cost analysis**; `az consumption usage list` is unreliable/preview). Gemini is the one genuinely new usage-based line item this sprint; volume at student-project scale is low but not zero, unlike the other reused free-tier resources.

---

## 3. Database Layer

### 3.1 Schema (`matching_db`)

Eleven tables, applied across migrations `001` through `011`. All eleven were confirmed present and holding real data as of today's final deployment (Section 3.3).

| Table | Purpose | Key columns |
|---|---|---|
| `image_descriptions` | Gemini job queue for one photo per lost/found item | `id`, `item_id`, `item_type`, `blob_url`, `processing_status`, `attempts`, `error_code`, `model_name`, `description`, `lease_token`, `lease_expires_at` |
| `matches` | Candidate/claimed matches between a lost and found item | `id`, `lost_item_id`, `found_item_id`, `status`, `is_active`, `claimant_role`, `lost_reporter_id`, `finder_id` |
| `match_actions` | Audit trail of confirm/reject decisions | `match_id`, `new_status`, `created_at` |
| `match_confirmation_outbox` | Outbox for the `matches.confirmed` Kafka event | (mirrors Item Service's outbox pattern) |
| `match_notifications` | Queue for claim/confirm/reject emails (Section 6) | `match_id`, `recipient_user_id`, `notification_type`, `status`, `attempts`, `next_attempt_at`, `error_code`, `sent_at` |
| `match_notification_activation` | Single-row cutover gate (`id = 1`, `activated_at`) so notifications never fire for matches older than the feature | `id`, `activated_at` |
| `match_reevaluation_jobs` | Re-run matching after an item or description changes | - |
| `matching_item_lifecycle_events` | Raw log of consumed Item Service lifecycle events | - |
| `matching_item_snapshots` | Latest known state per item, built from `items.*` events | - |
| `matching_item_states` | Current matching-relevant state per item | - |
| `notification_contacts` | Cached recipient email/active/deleted flags for notification delivery | `user_id`, `email`, `is_active`, `is_deleted` |

### 3.2 Migration process
Same gating as Auth and Item Service: `DbInitializer.RunPendingMigrations` is called only inside `if (app.Environment.IsDevelopment())` in `Program.cs`. It **never runs on Azure** (App Service defaults to `ASPNETCORE_ENVIRONMENT=Production`). All eleven migrations were applied manually via the `mysql` CLI against the live schema.

### 3.3 Database-name mismatch - resolved and documented (was an open checklist item)
`DbInitializer.cs` hardcodes an assertion that the connection string's database must be named exactly `matching_service`, and throws `InvalidOperationException` otherwise. The actual provisioned/deployed schema is `matching_db`. **This is confirmed harmless in Production**, for the same reason it's harmless that the migration logic exists at all in Production: it is gated to `Development` only (Section 3.2), so the assertion is never evaluated against the live database. It only matters for a developer running migrations locally against a database not literally named `matching_service` - worth a one-line comment in the code, but not a deployment blocker. No fix required for Production; a local-dev naming footgun only.

### 3.4 Outstanding migration review - resolved
The mid-sprint checklist listed only migrations `001`, `002`, `004`, `005` as applied and flagged a script named `003_AddProcessingIndexes.sql` as possibly still pending. Two corrections from today's final deployment:
- The actual `003` migration in the repository is `003_AddPhotoReplacementTracking.sql` - no file named `003_AddProcessingIndexes.sql` exists. The checklist's reference to that filename was a typo/stale reference, not a missing script.
- **All eleven migrations (`001`-`011`) are applied to the live `matching_db`.** Confirmed by direct inspection today: every table listed in Section 3.1 exists and several (`match_notification_activation`, `matching_item_states`, `match_reevaluation_jobs`, `notification_contacts`) contain real rows written by live application traffic, which is only possible if their creating migrations (`009`, `010`, `011`) ran successfully.

### 3.5 Network security
Same posture as Sprints 1-2: `SslMode=Required` enforced in the production connection string, `AllowAzureServices` firewall rule (`0.0.0.0-0.0.0.0`, permits App Service traffic regardless of outbound IP) plus IP-pinned rules for local dev access (re-add when the developer's residential IP changes - this was re-verified live today after an initial false alarm where the firewall rules appeared to have vanished from the Portal view; a page reload showed them intact and unchanged).

---

## 4. Event Streaming (Kafka)

Broker configuration and topic-prefix convention (`items.*`) unchanged from Sprint 2.

### 4.1 What Matching Service consumes
Matching Service is a **consumer only** of `items.*` events, via two independent consumer groups:
- **`ImageDescriptionEventHandler`** - triggered by item-created events (confirmed live: log lines `"Persisted 1 new image-description job(s) for Found item {id}"` / `"...for Lost item {id}"` fire within seconds of an item being created), queuing a Gemini description job per photo.
- **`ItemLifecycleConsumer`** (namespace `MatchingService.Lifecycle`) - keeps `matching_item_lifecycle_events`, `matching_item_snapshots`, and `matching_item_states` current as items are created, updated, resolved, or deleted, per the full `items.*` topic set Item Service documented in Sprint 2.

### 4.2 Consumer group ID
`Kafka:GroupId` is configured as `matching-service-image-descriptions` (per `appsettings.Production.json`) - the name only reflects the image-description consumer; the lifecycle consumer's own group ID was not independently confirmed this session and should be checked if consumer-lag issues ever arise.

### 4.3 What Matching Service produces: exactly one topic
| Topic | Producer | Triggered by | Consumed by |
|---|---|---|---|
| `matches.confirmed` | `MatchConfirmationPublisher` (drains `match_confirmation_outbox`) | A match reaching `CONFIRMED` status | Item Service's `ConfirmedMatchConsumer` → `ConfirmedMatchHandler`, which resolves both reports transactionally |

No `match.created` or `match.rejected` Kafka event exists. Those state transitions surface to users only via Matching Service's own HTTP API (polled by the frontend), never as Kafka events - confirmed by full-codebase search: `MatchConfirmationPublisher` is the only file in the `MatchingService` namespace using `IProducer`/`ProducerBuilder`/`ProduceAsync`.

### 4.4 Kafka broker connectivity noise - investigated, confirmed non-fatal
Both Item Service and Matching Service log a recurring warning approximately every 5 minutes:
```
%3|...|ERROR|rdkafka#producer-N| [thrd:...:9092/bootstrap]: 1/1 brokers are down
```
**Root cause identified this sprint:** the interval matches librdkafka's default topic-metadata refresh (`topic.metadata.refresh.interval.ms`, 300000ms/5min), landing shortly after Azure's outbound SNAT idle-connection timeout (~4 minutes) has already silently dropped the TCP connection. The client logs the stale connection as an error on its next scheduled check-in, then transparently reconnects.

**Confirmed non-fatal**, not by inference but by direct evidence: the item-service outbox table (`outbox_events`) shows every event in this period published successfully on the first attempt (`attempts = 0`, `last_error = NULL`), and Gemini jobs completed normally during windows where this warning was also firing. **Not yet mitigated** - `TopicMetadataRefreshIntervalMs` (recommend ~120000ms, under the SNAT window) has not been applied to either service's producer/consumer config. Recommended as a Sprint 4 cleanup item to reduce log noise; does not block sign-off.

### 4.5 Genuinely transient failures observed (self-healing, by design)
Distinct from the noise in 4.4: both `MatchConfirmationPublisher` and `ImageDescriptionWorker` were observed throwing real `MySqlException`s during a Kafka connectivity incident on `09-26` (a sustained ~2-hour period of `Connection setup timed out` at the broker level) and a brief, isolated recurrence on `09-28` at `18:52:53`. Both workers' outer catch blocks log (`MATCH_OUTBOX_FAILED` / `WORKER_CYCLE_FAILED`) and retry after a short delay - by design, not a bug. No data loss was observed in either incident; outbox rows eventually cleared and Gemini jobs eventually completed.

---

## 5. AI Integration - Image Descriptions (Gemini)

New for Sprint 3; neither Auth nor Item Service call an external AI provider.

### 5.1 Configuration
| Setting | Purpose |
|---|---|
| `Gemini__ApiKey` | Gemini API key |
| `Gemini__Model` | Model identifier, currently `gemini-3.5-flash-lite` |
| `BlobStorage__AllowedHost` | Restricts which blob host the service will download images from before sending to Gemini |

### 5.2 Job lifecycle
One row per photo per item, in `image_descriptions`. `ImageDescriptionWorker` claims jobs via a leased `UPDATE ... WHERE processing_status = 'PENDING' AND next_retry_at <= NOW()`, calls Gemini, and on failure schedules a retry with `attempts` incremented, up to `ImageProcessingSettings.MaxAttempts` (**5**, validated at startup to be between 1 and 10). After the fifth failed attempt, the job is left in `processing_status = 'FAILED'` with `next_retry_at = NULL` and **is never retried automatically** - there is no periodic sweep of permanently-failed jobs.

### 5.3 Live verification
- Six jobs completed successfully on first attempt during earlier testing (`gemini-3.5-flash-lite`, `attempts = 1`), and two more completed cleanly today (`18:39:24` and `18:45:16`, both within 20-30 seconds of being queued).
- One job (photo for a found item, phone) hit Gemini `503 AI_PROVIDER_SERVER_ERROR` (`Retryable: True`) five times over roughly 8 minutes and was left `FAILED`. Manually reset via `UPDATE ... SET processing_status='PENDING', attempts=0 ...` and retried; the retry also returned `503` on all 5 subsequent attempts within this session. Image download/validation logic (size cap, signature check) was ruled out as the cause - failure occurs after Gemini accepts the request, consistent with provider-side capacity rather than a malformed request.
- **Gap identified:** a permanently `FAILED` description job has no automatic recovery path and silently degrades that item's matchability (no description = weaker candidate matching signal) without surfacing anywhere in the UI. Recommended as a Sprint 4 follow-up: either a periodic sweep re-queuing old `FAILED` rows caused by provider errors, or a longer backoff/attempt budget.

---

## 6. Notification / Email Subsystem - confirmed working end-to-end

### 6.1 Intended design
Fully coded and unit-tested:
- `NotificationDiscoveryService.DiscoverAsync()` scans `matches`/`match_actions` every 15 seconds and inserts `match_notifications` rows: one `COUNTERPART_ACTION` email to the other party when one side confirms a claim, and one `MATCH_CONFIRMED` (or `MATCH_REJECTED`) email to **each** party once the match fully resolves.
- `NotificationRepository` leases and retries jobs (5 attempts, exponential backoff up to 30 minutes) and re-validates relevance immediately before sending (skips a stale job if the match state has since changed).
- `MatchEmailSender` sends via SMTP (SendGrid relay; `Smtp__Host/User/Password/FromAddress`, `Frontend__BaseUrl` required config), plain-text, one recipient per email, linking to `matched-items/{matchId}`.
- All of the above is orchestrated by `MatchNotificationWorker`, a `BackgroundService`.

### 6.2 Earlier diagnosis and correction
A mid-sprint pass through this document concluded that the subsystem had **never run in production**: `AddHostedService<MatchNotificationWorker>()` could not be found in `Program.cs` at the time, and a full download of the retained log history (back to `09-23`) showed zero `NOTIFICATION_CYCLE_FAILED` lines and zero "accepted by SMTP" successes for any match, despite several matches having reached `CONFIRMED`/`RESOLVED` in that window.

**Live testing after this sprint's final deployment round contradicts that conclusion** (Section 6.3) - notification jobs are now confirmed leasing, attempting, and sending successfully, which only happens if the worker is running. The evidence behind the original "never registered" finding was accurate as of the code and logs available at the time; it no longer reflects the deployed state. This document previously asserted, incorrectly, that no user had ever received a notification email - that claim is retracted. What actually blocked delivery is documented in Section 6.4.

### 6.3 Live verification (confirmed working)
A match (`234b7d17-...`) completed the full notification flow on a healthy instance:
- `COUNTERPART_ACTION` - **SENT** at `04:17:04`
- `MATCH_CONFIRMED` (both parties) - **SENT** at `04:19:54` and `04:19:55`

This confirms the full pipeline end to end: item creation → Kafka → matching → claim → confirmation → email delivery, with `NotificationRepository`'s leasing/attempt/status columns all behaving as designed.

### 6.4 Actual root cause of the earlier delivery failures: SendGrid sender-address typo
`Smtp__FromAddress` was configured as `back2ulosfoundapp@gmail.com` - missing the "t" in "lost" - which did not match the address verified with SendGrid's single-sender verification, so SendGrid rejected the sends. Corrected to `back2ulostfoundapp@gmail.com` in **both** Auth Service's and Matching Service's configuration. This, not a missing DI registration, was the actual blocker for real email delivery. (Migrations `009`-`011` and the matching Kafka topics were also confirmed present as prerequisites; see Sections 3.4 and 4.3.)

### 6.5 One known stale record (not a current bug)
Match `db0255e8-...`, created earlier this sprint before the sender-address fix, remains `status = FAILED`, `sent_at = NULL` - it exhausted its 5 retry attempts under the broken configuration. This is a dead test record from before the fix, not evidence of an ongoing problem; no match created after the fix has failed to send.

---

## 7. Match Confirmation & Item Resolution Flow

### 7.1 Cross-service transaction safety
`ConfirmedMatchHandler` (Item Service) processes each `matches.confirmed` event inside one MySQL transaction: an `INSERT IGNORE` into `match_resolution_inbox` keyed on `event_id` provides idempotency (a redelivered Kafka message is a no-op), followed by locking both reports (`SELECT ... FOR UPDATE`, lost then found, consistently ordered to avoid deadlock), validating owner/state, updating both to `RESOLVED`, and publishing `lost_item.resolved`/`found_item.resolved` - all committed or rolled back together.

### 7.2 Live verification this session
- A lost item and a found item (test data: a Samsung Galaxy phone) were matched with an observed similarity score, per the mid-sprint checklist.
- A claim was submitted and produced a `matches` row (`status = LOST_REPORTER_CONFIRMED`, `is_active = 1`) with `claimant_role = LOST`.
- The mid-sprint checklist separately recorded direct Kafka console-consumer verification of a `matches.confirmed` message containing match and item identifiers, and confirmed the topic exists.
- **Not completed this session:** the counterparty's (finder's) confirmation was not carried out, so this specific test match never reached `CONFIRMED`, and the item-resolution path (Section 7.1) was not re-exercised end-to-end against it today. The finder-rejection and lost-reporter-rejection paths were also not exercised. These remain open items (Section 9).

---

## 8. CI/CD Pipeline Design

Expected to follow the identical pattern established in Sprints 1-2 (`matching-service-ci-cd.yml`, path-filtered to `services/MatchingService/**` and `services/MatchingService.Tests/**`, `build-and-test` on every push to `develop`/`main`/`feature/**`, `deploy` gated to `develop`, publish profile as a GitHub secret, SCM Basic Auth Publishing Credentials enabled). **This was not independently re-confirmed during this session** - the mid-sprint checklist lists the workflow file name, trigger branches, deploy gating, and a successful run link as still-open items, and no CI/CD investigation was performed in this session's log/code review. Carrying forward as open (Section 9) rather than assumed complete.

---

## 9. Functional Verification Record

### 9.1 Verified live this sprint (mid-sprint checklist + this session, combined)
| Flow | Result |
|---|---|
| Kafka broker reachable from dev machine, port 9092 | Confirmed |
| Lost/found item creation → outbox → Kafka → image-description job persisted | Confirmed live, ~4-8 second end-to-end latency |
| Image description completion (Gemini) | Confirmed for 8 jobs across two sessions; `gemini-3.5-flash-lite`, `attempts = 1` on success |
| Candidate match preview with similarity score | Confirmed (87.05%), per mid-sprint checklist |
| Claim submission creates a `matches` row | Confirmed, `LOST_REPORTER_CONFIRMED` |
| `COUNTERPART_ACTION` notification queued and sent on claim | **Confirmed - SENT at `04:17:04`, verified live (Section 6.3)** |
| `matches.confirmed` topic exists and carries correct identifiers | Confirmed via Kafka console consumer, per mid-sprint checklist |
| Item resolution on match confirmation (`ConfirmedMatchHandler`) | Verified by code review this session; **not re-exercised live end-to-end this session** |
| `MATCH_CONFIRMED`/`MATCH_REJECTED` notifications | **Confirmed - both parties SENT, verified live (Section 6.3)** |

### 9.2 Explicitly outstanding (carried forward from the mid-sprint checklist, still open)
- Counterparty confirmation path, end-to-end
- Finder-rejection and lost-reporter-rejection paths
- Invalid / expired / duplicate / unauthorized match-decision handling
- Kafka delivery retry/recovery behavior under a deliberately induced failure (as opposed to the naturally-occurring incidents documented in Section 4.5)
- Item update/resolution event handling, downstream of a live confirmed match
- A full end-to-end repeat once the Section 6 fix is deployed
- CI/CD pipeline confirmation (Section 8)
- Application Insights wiring re-verification for Matching Service specifically (assumed to follow the Sprint 1-2 pattern - `AddApplicationInsightsTelemetry()` before `Build()`, `APPLICATIONINSIGHTS_CONNECTION_STRING` env var - but not independently queried/confirmed flowing this sprint)

---

## 10. Known Limitations & Deliberate Deferrals

- **Notification subsystem is confirmed working end-to-end** (Section 6) after correcting a SendGrid sender-address typo. One stale pre-fix match record (`db0255e8-...`) remains permanently `FAILED` - informational only, not a current bug.
- **Permanently-failed Gemini jobs have no recovery path** (Section 5.3) - silently degrades matching quality for the affected item with no UI signal.
- **Kafka idle-connection log noise is understood but not mitigated** (Section 4.4) - confirmed non-fatal, but adds operational noise; a one-line config change (`TopicMetadataRefreshIntervalMs`) would resolve it.
- **F1 (free tier) App Service instability, now diagnosed.** The container startup failure observed this sprint (`ContainerStartupFailure`, exit code 134) correlates with a subsequent ~9-minute outage window (`03:06`-`03:15` UTC / `8:36`-`8:45` AM local) during which Azure automatically replaced the unhealthy instance. This can recur unpredictably on F1. **Recommend upgrading to a paid tier (B1+) before any live demo.**
- **Kafka broker has no persistent volume** - a container restart wipes all topics; they are auto-recreated on next publish (assuming auto-create is enabled), but this produces the `UnknownTopicOrPart` consumer warnings seen after restarts. Worth flagging to whoever manages the broker container.
- **Matching Service's Kafka consumer group ID convention was not fully confirmed** (Section 4.2) - the lifecycle consumer's group ID specifically.
- **Broker remains `PLAINTEXT`, unauthenticated, and publicly exposed** - same accepted risk carried from Sprints 1-2, re-validated, not changed. Matching Service now also carries `HiddenInformation`-adjacent data (via consumed `items.*` events) across this same broker.
- **`DbInitializer.cs`'s hardcoded `matching_service` database-name check does not match the deployed `matching_db` schema** (Section 3.3) - confirmed harmless in Production (Development-gated), a local-dev-only footgun worth a comment but not a fix.
- Admin Verify Service is not yet built (Sprint 4).

---

## 11. AI Assistant Usage

AI tools were used throughout this sprint for infrastructure debugging, log/database investigation, and code-level root-cause analysis of the Matching Service.

Specific areas where AI assistance was used:
- Diagnosing the recurring Kafka "brokers are down" warning across Item and Matching Service logs, down to the SNAT-timeout/metadata-refresh-interval mechanism (Section 4.4)
- Diagnosing and manually recovering a permanently-failed Gemini image-description job, including ruling out image-validation causes (Section 5.3)
- Tracing the full match-confirmation and item-resolution data path across both services' code and live database state (Section 7)
- Investigating the notification subsystem via systematic log-history and codebase search, which at the time found no DI/hosted-service registration and no evidence of any notification ever being sent (Section 6.2) - later live testing after further fixes showed the subsystem does run and deliver successfully, and the actual blocker was a SendGrid sender-address typo (Section 6.4); this document was corrected accordingly rather than left with the earlier, superseded conclusion
- Reconciling the mid-sprint deployment checklist against today's final deployment state (migrations, database-name mismatch) (Section 3.3-3.4)
- Diagnosing the F1-tier container crash/outage pattern and correlating it with the exit-code-134 startup failure (Section 10)
- Drafting and organizing this documentation set, cross-referenced against the Sprint 1 and Sprint 2 documentation for structural and terminology consistency, and revising it as new evidence emerged

## 12. Reference Documents

- `Sprint3-Matching-Service-Deployment-Checklist.md` - mid-sprint checklist snapshot; superseded in detail by this document but retained as a dated record of what was verified before the final deployment
- `Sprint2-DevOps-Technical-Documentation.md` - Item Service; establishes the `items.*` event catalogue Matching Service consumes, and the outbox pattern Matching Service's `match_confirmation_outbox` mirrors
- `Sprint1-DevOps-Technical-Documentation.md` - Auth Service baseline; shared broker/MySQL/App Insights setup, Kafka noise baseline, branch strategy
- `Azure-DevOps-Setup-Reference.md` - full step-by-step infra setup (CLI + Portal)
- `Secrets-Map-AI-Safe-Reference.md` - credential inventory safe to share with AI assistants (no real values)
