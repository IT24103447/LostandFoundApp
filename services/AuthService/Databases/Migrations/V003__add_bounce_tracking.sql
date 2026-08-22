-- V003: bounce tracking — bounced_at on tokens + email_bounces audit table
-- Idempotent: safe to re-run. Applied dev-only via DbInitializer in Development.

USE auth_service;

-- Idempotent: add bounced_at to email_verification_tokens
SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'email_verification_tokens'
      AND COLUMN_NAME = 'bounced_at'
);
SET @sql := IF(@col_exists = 0,
    'ALTER TABLE email_verification_tokens ADD COLUMN bounced_at DATETIME(3) NULL',
    'SELECT 1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Idempotent: add email_bounced_at to users
SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'users'
      AND COLUMN_NAME = 'email_bounced_at'
);
SET @sql := IF(@col_exists = 0,
    'ALTER TABLE users ADD COLUMN email_bounced_at DATETIME(3) NULL',
    'SELECT 1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Audit table for SendGrid bounce events
CREATE TABLE IF NOT EXISTS email_bounces (
    id            CHAR(36)     NOT NULL PRIMARY KEY,
    user_id       CHAR(36)     NULL,
    email         VARCHAR(254) NOT NULL,
    event_type    VARCHAR(32)  NOT NULL,
    reason        VARCHAR(255) NULL,
    sg_message_id VARCHAR(128) NULL,
    occurred_at   DATETIME(3)  NOT NULL,
    received_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    raw_payload   JSON         NULL,
    INDEX ix_eb_email (email),
    INDEX ix_eb_user_id (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
