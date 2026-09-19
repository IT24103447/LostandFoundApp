-- V007: create outbox_events table for ItemService (transactional outbox)
--
-- Domain events are inserted here in the SAME database transaction as the item
-- change that caused them. A background relay (OutboxRelayService) reads pending
-- rows and publishes them to Kafka, so an event is never lost because the broker
-- was down or the app crashed after the DB commit.
--
-- Delivery is at-least-once: consumers must de-duplicate on the event's EventId
-- (also used as the Kafka message key).
--
--   published_at IS NULL     => still pending (or being retried)
--   published_at NOT NULL    => delivered to Kafka; purged after the retention window
--   attempts >= MaxAttempts  => gave up; kept for manual inspection (see last_error)
--   locked_by / locked_until => short lease so several app instances never
--                               publish the same row at the same time

USE item_service;

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
