-- Item Service migration V007 (transactional outbox) for Azure's live `item_db` schema.
-- Run manually BEFORE the deploy that ships the outbox - nothing auto-runs in
-- Production, DbInitializer is Development-only. If this is not run first, every
-- create / edit / resolve / delete request fails (the event insert has no table
-- to write to) and the relay logs errors until the table exists.
--
-- This is the production equivalent of the embedded development script
-- services/ItemService/Databases/Migrations/V007__create_outbox_events_table.sql,
-- rewritten for the real provisioned schema. Do NOT run the embedded script
-- against Azure as-is, or MySQL will create an empty `item_service` database.
--
-- What this does:
--   * creates outbox_events (pending / delivered domain events for Kafka)
--   * creates its supporting index
--   * touches no existing table and no existing row
--
-- Idempotent: safe to re-run (CREATE TABLE IF NOT EXISTS).
--
-- Run:
--     Get-Content DevOps_Documentation\azure-item_db-migration-v007.sql |
--       mysql -h lostfound-mysql.mysql.database.azure.com -u <admin> -p item_db

USE item_db;

CREATE TABLE IF NOT EXISTS outbox_events (
    id              CHAR(36)     NOT NULL PRIMARY KEY,
    topic           VARCHAR(255) NOT NULL,
    message_key     VARCHAR(64)  NOT NULL,
    payload         MEDIUMTEXT   NOT NULL,
    created_at      DATETIME(3)  NOT NULL,
    next_attempt_at DATETIME(3)  NOT NULL,
    attempts        INT          NOT NULL DEFAULT 0,
    last_error      VARCHAR(500) NULL DEFAULT NULL,
    locked_by       CHAR(36)     NULL DEFAULT NULL,
    locked_until    DATETIME(3)  NULL DEFAULT NULL,
    published_at    DATETIME(3)  NULL DEFAULT NULL,
    INDEX ix_outbox_events_pending (published_at, next_attempt_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---- verification -----------------------------------------------------------

SELECT table_name, column_name, column_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'item_db'
  AND table_name = 'outbox_events'
ORDER BY ordinal_position;
