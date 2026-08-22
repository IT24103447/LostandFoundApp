-- V002: create email_verification_tokens table and add users.last_resent_at
-- Idempotent: safe to re-run. Applied dev-only via DbInitializer in Development.

USE auth_service;

CREATE TABLE IF NOT EXISTS email_verification_tokens (
    id            CHAR(36)     NOT NULL PRIMARY KEY,
    user_id       CHAR(36)     NOT NULL,
    code_hash     CHAR(64)     NOT NULL UNIQUE,
    pending_email VARCHAR(254) NULL,
    expires_at    DATETIME(3)  NOT NULL,
    attempts      INT          NOT NULL DEFAULT 0,
    used_at       DATETIME(3)  NULL,
    created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    INDEX ix_evt_user_id (user_id),
    INDEX ix_evt_expires_at (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Add cooldown column to users. Idempotent via INFORMATION_SCHEMA check.
SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'users'
      AND COLUMN_NAME = 'last_resent_at'
);
SET @sql := IF(@col_exists = 0,
    'ALTER TABLE users ADD COLUMN last_resent_at DATETIME(3) NULL',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
