# QA Test Plan — "Report a Lost Item" (Sprint 2)

**Story:** As a user, I want to report an item I've lost, so that other users can identify and return it if found.
**Components under test:** `ItemService` (.NET 8 / MySQL / Kafka) + `ReportLostItemWizard` (React Hook Form + Zod)
**Tools:** xUnit/NUnit (unit), Postman/xUnit+WebApplicationFactory (API/integration), Selenium (E2E UI), JMeter (performance/load), OWASP ZAP or manual (security)

---

## 1. Traceability to Acceptance Criteria

| AC Scenario | Covered By |
|---|---|
| 1 — Successful report creation | UNIT-01 to UNIT-04, API-01, API-02, E2E-01 |
| 2 — Optional photos provided | UNIT-05, UNIT-06, API-05 to API-08, E2E-04 |
| 3 — Missing required field | UNIT-07, API-09 to API-13, E2E-05 |
| 4 — Invalid field value | UNIT-08 to UNIT-11, API-14 to API-19, E2E-06 |
| Hidden info never exposed | UNIT-12, API-20, SEC-06 |
| Kafka event published w/ hidden info | UNIT-13, API-21, API-22 |
| Auth dependency (valid user_id) | API-23 to API-25, SEC-01 to SEC-03 |

---

## 2. Unit Test Cases (xUnit / NUnit — target: Controller, Repository, Services, DTO mapping)

| ID | Test | Target | Expected Result | Priority |
|---|---|---|---|---|
| UNIT-01 | Valid request builds `LostItem` with `Status = ACTIVE` | `LostItemsController.ReportLostItem` | Item object has Status ACTIVE, correct UserId from JWT claim, CreatedAt/UpdatedAt set to UtcNow | High |
| UNIT-02 | `ToDto` mapping never includes `HiddenInformation` | `LostItemsController.ToDto` | Reflect over `LostItemResponseDto` fields, assert no property maps from `HiddenInformation` | Critical |
| UNIT-03 | Valid `DateLost` string ("2026-08-28") parses correctly | Controller date parsing | `DateOnly.TryParseExact` succeeds, no validation error added | High |
| UNIT-04 | Repository `CreateAsync` sends correct SQL params | `LostItemsRepository.CreateAsync` | Mock/inspect params: Id, UserId, Title, Category, Description, DateLost, LastKnownLocation, HiddenInformation, Status all bound correctly | High |
| UNIT-05 | `AddPhotosAsync` with empty list is a no-op | `LostItemsRepository.AddPhotosAsync` | No DB command executed when `photoUrls` is empty | Medium |
| UNIT-06 | `AddPhotosAsync` inserts one row per photo URL with unique IDs | `LostItemsRepository.AddPhotosAsync` | N insert calls for N URLs, each with a distinct Guid | Medium |
| UNIT-07 | Missing `Title`/`Category`/`Description`/`LastKnownLocation`/`HiddenInformation` fails model validation | DataAnnotations on `ReportLostItemRequest` | ModelState invalid, automatic 400 via `[ApiController]` filter, no controller code executes | Critical |
| UNIT-08 | `DateLost` in wrong format (e.g. `08/28/2026`) fails parse | Controller | `errors["DateLost"]` populated, `ValidationProblem` returned | High |
| UNIT-09 | `DateLost` set to tomorrow is rejected | Controller | `errors["DateLost"]` = "cannot be in the future" | High |
| UNIT-10 | `DateLost` set to exactly today is accepted | Controller | No date error added (boundary case) | Medium |
| UNIT-11 | Title/Category/Description/Location/HiddenInformation each exactly at max length (150/50/2000/255/500) is accepted | DataAnnotations `[MaxLength]` | ModelState valid at exact boundary | Medium |
| UNIT-12 | Title/Category/Description/Location/HiddenInformation each 1 char over max length is rejected | DataAnnotations | ModelState invalid, 400 returned | High |
| UNIT-13 | `LostItemCreatedEvent` payload contains `HiddenInformation` field | Event construction in controller | Serialized event JSON includes non-empty `hiddenInformation` | Critical |
| UNIT-14 | Whitespace-only string (`"   "`) passed as Title | DataAnnotations `[Required]` | **Known risk:** `[Required]` with default `AllowEmptyStrings=false` blocks `""` but NOT whitespace-only strings — verify current behavior and flag if it passes (see BUG-01) | High |
| UNIT-15 | Photo count exceeds `MaxPhotosPerItem` | Controller manual validation | `errors["Photos"]` = "at most N photos" message, request rejected | High |
| UNIT-16 | Photo file size exceeds `MaxPhotoSizeBytes` (5MB) | Controller manual validation | `errors["Photos"]` size message returned | High |
| UNIT-17 | Photo with disallowed content type (e.g. `application/pdf`) | Controller manual validation | `errors["Photos"]` = "must be JPEG, PNG, or WEBP" | High |
| UNIT-18 | Zero-length photo file (`photo.Length == 0`) | Controller manual validation | `errors["Photos"]` = "file is empty", loop breaks correctly, no false positives on other valid photos | Medium |
| UNIT-19 | `KafkaEventPublisher.PublishAsync` when channel is full | `KafkaEventPublisher` | `TryWrite` returns false, warning logged, method still returns completed task (does NOT throw) — confirm this "silent drop" behavior explicitly (see BUG-02) | Critical |
| UNIT-20 | `GetByIdAsync` returns null for nonexistent item | `LostItemsRepository.GetByIdAsync` | Returns null, controller maps to 404 | Medium |

**Target unit coverage:** Controller validation branches, DTO mapping, repository parameter binding, and event publisher failure path should be **≥80% line coverage** — this logic is the core business value of the story and is cheap to unit test in isolation with mocked `IDbConnectionFactory` / `IEventPublisher`.

---

## 3. API / Integration Test Cases (Postman / xUnit + `WebApplicationFactory` / MySQL test container)

| ID | Test | Steps | Expected Result | Priority |
|---|---|---|---|---|
| API-01 | Happy path — full valid payload, no photos | POST `/api/items/lost` with valid cookie + all fields | 201 Created, response body has Id/Title/Category/Status="ACTIVE"/CreatedAt, `HiddenInformation` key absent entirely | Critical |
| API-02 | Happy path — verify DB row | After API-01, query `lost_items` table directly | Row exists with correct `user_id`, `status='ACTIVE'`, `hidden_information` stored as submitted | Critical |
| API-03 | GET by ID after creation | GET `/api/items/lost/{id}` right after creating | 200 OK, same data as create response, no hidden info field | High |
| API-04 | GET by ID for nonexistent GUID | GET with random GUID | 404, `{"error":"Lost item not found."}` | Medium |
| API-05 | Submit with 1 photo | POST multipart with 1 valid JPEG | 201, `photoUrls` array has 1 URL, `lost_item_photos` row created with FK to item | High |
| API-06 | Submit with max allowed photos (boundary, e.g. 5) | POST with exactly `MaxPhotosPerItem` photos | 201, all photos stored | High |
| API-07 | Submit with `MaxPhotosPerItem + 1` photos | POST with 6 photos when max is 5 | 400, `errors.Photos` message, no item OR photos persisted (verify no partial write) | Critical |
| API-08 | Submit with zero photos | POST with `Photos` field omitted entirely | 201, `photoUrls` is empty array, item still created (photos truly optional) | High |
| API-09 | Missing Title | POST with Title omitted | 400 ValidationProblem, error keyed to `Title` | Critical |
| API-10 | Missing Category | POST with Category omitted | 400, error keyed to `Category` | Critical |
| API-11 | Missing Description | POST with Description omitted | 400, error keyed to `Description` | Critical |
| API-12 | Missing LastKnownLocation | POST with field omitted | 400, error keyed to `LastKnownLocation` | Critical |
| API-13 | Missing HiddenInformation | POST with field omitted | 400, error keyed to `HiddenInformation` | Critical |
| API-14 | Title exceeds 150 chars | POST with 151-char title | 400, MaxLength error | High |
| API-15 | Description exceeds 2000 chars | POST with 2001-char description | 400, MaxLength error | High |
| API-16 | HiddenInformation exceeds 500 chars | POST with 501-char value | 400, MaxLength error | High |
| API-17 | DateLost malformed string | POST `DateLost="2026/08/28"` | 400, `errors.DateLost` | High |
| API-18 | DateLost is a future date | POST `DateLost` = tomorrow | 400, "cannot be in the future" | High |
| API-19 | Photo with wrong content type disguised with `.jpg` extension | Upload a `.txt` renamed to `.jpg` with real `Content-Type: text/plain` | 400 rejected on `ContentType` check (confirms check isn't relying on file extension alone) | High |
| API-20 | Response contract check across ALL endpoints | Inspect raw JSON of POST 201 response AND GET 200 response | `hiddenInformation` / `hidden_information` key does not appear anywhere in either payload, case-insensitive check | Critical |
| API-21 | Kafka event fires on successful creation | POST valid item, consume from test topic `{prefix}.lost_item.created` | Event received within timeout, `EventType = "lost_item.created"`, `LostItemId` matches | Critical |
| API-22 | Kafka event contains hidden information | Inspect consumed event payload from API-21 | `hiddenInformation` field present and matches submitted value | Critical |
| API-23 | No auth cookie provided | POST with no `auth_token` cookie | 401 Unauthorized | Critical |
| API-24 | Expired JWT | POST with an expired `auth_token` | 401 Unauthorized (via `ValidateLifetime`) | Critical |
| API-25 | Tampered JWT signature | POST with a JWT whose payload was modified but signature not re-signed | 401 Unauthorized (via `ValidateIssuerSigningKey`) | Critical |
| API-26 | Malformed multipart body (e.g. missing boundary) | POST corrupted multipart | 400 Bad Request, no 500 | Medium |
| API-27 | Category value not in the frontend's known category list | POST arbitrary string like `"asdf123"` as Category directly to API | Currently accepted (no server-side enum check) — flag as design gap, see BUG-03 | Medium |

---

## 4. UI / E2E Test Cases (Selenium — 3-step wizard)

| ID | Test | Steps | Expected Result | Priority |
|---|---|---|---|---|
| E2E-01 | Full happy-path submission across all 3 steps | Fill Step 1 (title, category, description) → Continue → Step 2 (date, location) → Continue → Step 3 (hidden info, optional photo) → Submit | Redirects to `/report-lost-item/success`, summary shows correct title/category/date/location/status ACTIVE | Critical |
| E2E-02 | "Continue" blocked when Step 1 required fields empty | Leave Title blank, click Continue | Stays on Step 1, inline error shown under Title field, focus/scroll to error | High |
| E2E-03 | Category dropdown selection updates icon + closes on outside click | Open `CategorySelect`, pick a category, click elsewhere | Selected category shown with icon, dropdown closes | Medium |
| E2E-04 | Photo dropzone: drag-and-drop and click-to-browse both work | Drag a valid JPEG onto dropzone; separately use "Browse files" | Both methods add a thumbnail preview; count increments correctly | High |
| E2E-05 | Photo dropzone rejects disallowed file type silently per `ALLOWED_PHOTO_TYPES` filter | Attempt to drop a `.pdf` | File is filtered out client-side, not added to preview list, no crash | Medium |
| E2E-06 | Photo dropzone caps at `MAX_PHOTOS` | Add more photos than `MAX_PHOTOS` | Upload UI hides "add more" control once limit reached, no crash, extra files silently dropped from the batch | Medium |
| E2E-07 | Date field cannot select a future date | Open native date picker on Step 2 | `max` attribute equals today; future dates disabled in picker UI | High |
| E2E-08 | Server-side validation errors map back to correct wizard step | Submit valid client-side data, but simulate a 422 response with `errors.HiddenInformation` (e.g. via mocked network response) | Wizard jumps back to Step 3, error shown on Hidden Information field | High |
| E2E-09 | Expired session during submission | Submit with a stale/expired auth cookie | `submitError` shows "Your session has expired. Please sign in again." message, no crash | Medium |
| E2E-10 | Success page renders hidden info as masked, never in plaintext | Reach success page after E2E-01 | Page shows "Hidden" label with lock icon, never the actual value in DOM (`view-source` / `page source` check) | Critical |
| E2E-11 | Back/Cancel navigation preserves entered data going Step 2 → Step 1 | Fill Step 1, go to Step 2, click Back | Step 1 fields still populated with previously entered values | Low |
| E2E-12 | Description live character counter | Type into Description field | Counter updates in real time, shows `x / 1000` (note: **mismatch check** — model allows up to 2000 chars server-side but UI counter caps display at 1000, see BUG-04) | Medium |

---

## 5. Security Test Cases

| ID | Test | Expected Result | Priority |
|---|---|---|---|
| SEC-01 | Auth bypass — call POST directly with no cookie | 401, no data written to DB | Critical |
| SEC-02 | JWT `alg: none` attack | POST with a JWT crafted using `alg: none` | 401 rejected, `ValidateIssuerSigningKey` prevents it | Critical |
| SEC-03 | Cross-user submission — use user A's valid token, confirm item is created under user A's ID only | Item `user_id` in DB matches token subject, cannot be spoofed via a form field | Critical |
| SEC-04 | Stored XSS via Title/Description/Location fields | Submit `<script>alert(1)</script>` in each free-text field, then view via GET/any consumer | Data stored as-is (parameterized query prevents SQL injection) but confirm consuming frontend escapes/encodes output rather than rendering raw HTML | Critical |
| SEC-05 | SQL injection attempt in Title (e.g. `'; DROP TABLE lost_items;--`) | POST malicious string as Title | Stored as literal text via parameterized `MySqlCommand`, table unaffected | Critical |
| SEC-06 | Hidden information exposure sweep | Grep every response body from every endpoint (POST, GET by id, and any future list/search endpoint) for the submitted hidden info value | Value never appears outside the DB and the Kafka event | Critical |
| SEC-07 | CORS — request from disallowed origin | Send request with `Origin` header not in `Cors:AllowedOrigins` | Request blocked by CORS policy | Medium |
| SEC-08 | File upload — disguised executable | Upload a `.jpg`-named file containing an actual `.exe`/script with correct `image/jpeg` content-type header spoofed | Confirm whether content sniffing / magic-byte validation exists beyond the `ContentType` header check — if not, flag as a gap (see BUG-05) | High |
| SEC-09 | Oversized request body (many large photos combined) | POST multiple photos near/at max size to test aggregate request size limits | Server rejects gracefully (413 or 400), not a crash/hang | Medium |
| SEC-10 | Path traversal via filename | Upload photo with filename `../../evil.jpg` | `LocalPhotoStorageService` uses a generated GUID filename, not the original — confirm original filename is never used for the path | High |

---

## 6. Performance / Load Test Cases (JMeter)

| ID | Test | Setup | Expected Result | Priority |
|---|---|---|---|---|
| PERF-01 | Baseline single-user response time | 1 user, POST with no photos | p95 response time under an agreed SLA (e.g. 500ms) | Medium |
| PERF-02 | Concurrent submissions, no photos | 50 concurrent users, ramp-up 10s, POST loop | No errors, DB writes all succeed, no deadlocks on `lost_items` insert | High |
| PERF-03 | Concurrent submissions with photos | 50 concurrent users each uploading 3 photos (~2MB each) | Local/blob storage handles concurrent writes, response times degrade gracefully not catastrophically | High |
| PERF-04 | Kafka publisher channel saturation | Fire >1000 events faster than the Kafka producer can drain the bounded channel (capacity 1000, `DropWrite` mode) | Confirm items are still created successfully in DB even when events get silently dropped — this proves BUG-02's real-world impact under load | Critical |
| PERF-05 | Sustained soak test | 20 concurrent users submitting continuously for 30+ minutes | No memory leak, no connection pool exhaustion on MySQL connections (each repo call opens a new connection) | Medium |
| PERF-06 | Spike test on GET by id | Sudden spike to 200 concurrent GETs on a known item id | Response times stay reasonable, no 5xx errors | Low |

---

## 7. Sample Bug Reports (found via code review during test design)

**BUG-01 — Whitespace-only required fields may pass validation**
- Severity: Medium | Priority: High
- `[Required]` on `ReportLostItemRequest` string fields blocks `null`/`""` but not whitespace-only input (e.g. `"   "`) by default in most DataAnnotations configurations.
- Impact: a user could submit a lost item report with a blank-looking title.
- Repro: POST with `Title=" "` (single space) and all other fields valid.
- Suggested fix: custom validation attribute or manual `.Trim()` + length check server-side.

**BUG-02 — Kafka event loss is silent on channel overflow**
- Severity: High | Priority: High
- `KafkaEventPublisher` uses `BoundedChannelOptions(1000)` with `FullMode = BoundedChannelFullMode.DropWrite`. If the channel fills up, `TryWrite` fails, a warning is logged, but the HTTP response to the user is still 201 Created.
- Impact: user believes their report was fully processed and will be matched, but the Matching Service never receives the event. This directly undermines the story's core business value ("Feeds the Matching Service directly").
- Suggested fix: alerting on drop events, backpressure, or a persistent outbox pattern instead of an in-memory channel.

**BUG-03 — Category has no server-side enum validation**
- Severity: Low | Priority: Medium
- Frontend restricts Category to a fixed list (`LOST_ITEM_CATEGORIES`), but the API only checks `[MaxLength(50)]`. Any client bypassing the UI (Postman, mobile app, etc.) can submit an arbitrary category string.
- Impact: inconsistent/dirty category data reaching the Matching Service.

**BUG-04 — Description length mismatch between frontend and backend**
- Severity: Low | Priority: Low
- `StepItemDetails.tsx` shows a "`x / 1000`" character counter, but the backend allows up to 2000 chars (`[MaxLength(2000)]`) and the DB column is `VARCHAR(2000)`.
- Impact: confusing UX; users may think 1000 is the hard limit.

**BUG-05 — Photo type validation relies solely on the `Content-Type` header**
- Severity: Medium | Priority: High
- Neither the controller nor `LocalPhotoStorageService`/`AzureBlobPhotoStorageService` inspect file magic bytes; validation trusts the client-supplied `ContentType`.
- Impact: a malicious file can be uploaded with a spoofed `Content-Type: image/jpeg` header.
- Suggested fix: server-side content sniffing (e.g. checking file signature) before accepting the upload.

---

## 8. Coverage Summary

- **Unit tests:** 20 cases targeting controller validation branches, DTO mapping, repository, and event publisher — this is the highest-value area to hit ≥80% line coverage since it's cheap to isolate with mocks.
- **API/Integration tests:** 27 cases covering all 4 AC scenarios plus the DoD items (hidden info exposure, Kafka event contents, auth dependency).
- **E2E/UI tests:** 12 cases covering the full wizard flow, client-side validation, and error-mapping from server back to the correct step.
- **Security tests:** 10 cases covering auth bypass, injection, file upload abuse, and the hidden-information leak surface specifically (this field is the whole point of the story, so it gets extra scrutiny).
- **Performance tests:** 6 cases, including one designed specifically to prove out BUG-02 under real load.

**Total: 75 test cases**, well past the 20 minimum, with explicit traceability back to every acceptance criterion and Definition-of-Done item in the story.
