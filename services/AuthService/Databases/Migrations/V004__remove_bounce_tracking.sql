-- V004: remove bounce tracking
-- Idempotent: safe to re-run.

USE auth_service;

-- Idempotent: drop bounced_at from email_verification_tokens
SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'email_verification_tokens'
      AND COLUMN_NAME = 'bounced_at'
);
SET @sql := IF(@col_exists > 0,
    'ALTER TABLE email_verification_tokens DROP COLUMN bounced_at',
    'SELECT 1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Idempotent: drop email_bounced_at from users
SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'users'
      AND COLUMN_NAME = 'email_bounced_at'
);
SET @sql := IF(@col_exists > 0,
    'ALTER TABLE users DROP COLUMN email_bounced_at',
    'SELECT 1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- Drop email_bounces table
DROP TABLE IF EXISTS email_bounces;
