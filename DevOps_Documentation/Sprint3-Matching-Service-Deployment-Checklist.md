# Sprint 3 - Matching Service Deployment Checklist

**Scope:** This checklist records the deployment, infrastructure configuration, production configuration, integration verification, and operational checks completed for the Matching Service during Sprint 3.

**Service:** Matching Service  
**Sprint:** 3  
**Environment:** Azure / Production  
**Status:** Completed

---

## Sprint 3 Deployment Background

- [x] Matching Service code was available on the `develop` branch and build/tests were completed successfully.
- [x] Existing Azure infrastructure from Sprints 1 and 2 was reused where applicable.
- [x] Matching Service was deployed incrementally as completed stories/features became available for verification.
- [x] Multiple deployment checkpoints were used during the sprint to identify integration and configuration issues before final completion.
- [x] Final Matching Service implementation was deployed and verified in the live environment.

---

## Azure Infrastructure

- [x] Reused resource group `lostfound-rg`.
- [x] Reused App Service Plan `lostfound-plan`.
- [x] Created and configured Matching Service App Service `lostfound-matching-service`.
- [x] Configured the App Service to run Linux with .NET 8.
- [x] Reused Azure MySQL Flexible Server `lostfound-mysql`.
- [x] Reused the existing Kafka broker `lostfound-kafka`.
- [x] Reused Application Insights resource `lostfound-insights`.
- [x] Verified that the deployed Matching Service started successfully in Azure.
- [x] Verified the service startup probe completed successfully.

---

## Database Deployment

**Database:** `matching_db`

- [x] Created/initialized the Matching Service database on the existing Azure MySQL Flexible Server.
- [x] Applied the required Matching Service database migrations.
- [x] Verified the required database tables exist in the live database.
- [x] Verified database connectivity from the deployed Matching Service.
- [x] Configured the required MySQL connection settings for the production environment.
- [x] Configured the development IP firewall rule when direct local database verification was required.
- [x] Verified live database records were being created by the deployed application.
- [x] Confirmed the production database uses `matching_db`.

---

## App Service Configuration

**App Service:** `lostfound-matching-service`

- [x] Configured production environment variables through Azure App Service configuration.
- [x] Configured MySQL connection settings.
- [x] Configured Kafka connection settings.
- [x] Configured Gemini API settings.
- [x] Configured Blob Storage settings.
- [x] Configured Application Insights connection settings.
- [x] Verified that sensitive configuration values were not committed to the repository.
- [x] Verified the deployed application successfully loaded its production configuration.
- [x] Verified the App Service remained operational after deployment.

---

## Kafka Integration

- [x] Reused the existing Azure-hosted Kafka broker.
- [x] Verified Kafka broker reachability.
- [x] Verified Matching Service connectivity to Kafka.
- [x] Verified consumption of `items.*` events required by Matching Service.
- [x] Confirmed the `matches.confirmed` topic exists.
- [x] Verified that real `matches.confirmed` events were published by Matching Service.
- [x] Consumed `matches.confirmed` events using a Kafka console consumer.
- [x] Verified published events contained the expected match and item identifiers.
- [x] Verified previously generated confirmation events could be inspected directly from the Kafka topic.
- [x] Verified the Matching Service outbox records associated with confirmed matches.
- [x] Verified that a confirmation event successfully reached Kafka after being retried through the outbox mechanism.
- [x] Observed and investigated intermittent Kafka connection warnings.
- [x] Confirmed that the observed Kafka connection warnings did not prevent successful event delivery during verification.

---

## AI / Gemini Integration

- [x] Configured Gemini API settings in the production environment.
- [x] Verified Matching Service could communicate with the Gemini API.
- [x] Verified image-description jobs were created from item events.
- [x] Verified completed image-description records in `matching_db`.
- [x] Verified successful image-description processing in the deployed environment.
- [x] Observed a temporary Gemini provider error during testing.
- [x] Verified retry/backoff behavior following the temporary provider failure.

---

## Notification / Email Delivery

- [x] Configured Matching Service SMTP settings in Azure.
- [x] Reused the existing SendGrid SMTP configuration.
- [x] Identified a sender-address configuration issue during deployment verification.
- [x] Corrected the sender-address configuration.
- [x] Redeployed/restarted the service after configuration correction.
- [x] Verified successful notification processing through the `match_notifications` table.
- [x] Verified `MATCH_CONFIRMED` notification records reached `SENT` status.
- [x] Verified `sent_at` timestamps were recorded for successfully delivered notifications.
- [x] Verified confirmation notifications were sent to both parties.
- [x] Cross-checked notification delivery using the SendGrid activity records.

---

## End-to-End Deployment Verification

- [x] Item creation generated the required downstream event flow.
- [x] Matching Service received item information through Kafka.
- [x] Image-description processing completed successfully.
- [x] Candidate matching produced a similarity score.
- [x] Claim submission created a match record.
- [x] Match confirmation generated a `matches.confirmed` event.
- [x] `matches.confirmed` was successfully published to Kafka.
- [x] Confirmation notification records were created.
- [x] Confirmation notifications reached `SENT` status.
- [x] The complete deployed flow was verified across Matching Service, Kafka, database, and notification components.

---

## CI/CD

- [x] Matching Service CI/CD workflow was configured.
- [x] Workflow path filters target Matching Service source and test files.
- [x] Build and test execution was included in the pipeline.
- [x] Deployment was configured for the intended deployment branch.
- [x] Azure deployment was successfully performed through the configured deployment process.
- [x] Deployment was verified after the application was running in Azure.

---

## Observability

- [x] Application Insights was connected to Matching Service.
- [x] Production application logs were inspected during deployment verification.
- [x] Azure startup logs were inspected when troubleshooting deployment issues.
- [x] Kafka connection warnings were investigated using application logs.
- [x] Database state was inspected directly to verify application processing.
- [x] Kafka topic contents were inspected to verify event delivery.
- [x] Notification database records were inspected to verify email processing.

---

## Deployment Issues Identified and Resolved

- [x] Investigated an initial App Service startup failure.
- [x] Confirmed a later deployment successfully started and passed the startup probe.
- [x] Investigated intermittent Kafka connectivity warnings.
- [x] Verified successful Kafka communication after the connection warnings.
- [x] Investigated notification delivery failures.
- [x] Identified the incorrect SendGrid sender-address configuration.
- [x] Corrected the sender configuration and verified successful notification delivery.
- [x] Used incremental deployments/checkpoints to identify configuration and integration issues before final sprint completion.

---

## Known Limitations / Follow-Up Items

- [x] Kafka metadata-refresh connection warnings were identified and documented.
- [ ] Kafka connection warning cleanup/configuration improvement can be considered for Sprint 4.
- [ ] A periodic sweep for permanently failed Gemini jobs can be considered for Sprint 4.
- [ ] App Service plan capacity can be reviewed before a high-load/live demonstration.
- [ ] Additional production observability queries can be added if required.
- [ ] Admin Verify Service remains outside the Sprint 3 Matching Service deployment scope.

---

## Final Deployment Status

- [x] Matching Service deployed successfully to Azure.
- [x] Production configuration verified.
- [x] Database connectivity verified.
- [x] Kafka connectivity and event delivery verified.
- [x] Gemini integration verified.
- [x] Email notification delivery verified.
- [x] End-to-end integration flow verified.
- [x] Operational issues encountered during deployment were investigated and documented.
- [x] Sprint 3 deployment verification completed.