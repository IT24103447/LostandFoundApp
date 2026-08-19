-- V001: create users table for AuthService
-- Idempotent: safe to re-run. Applied dev-only via DbInitializer in Development.

CREATE DATABASE IF NOT EXISTS auth_service;
USE auth_service;

CREATE TABLE IF NOT EXISTS users (
    id                CHAR(36)     NOT NULL PRIMARY KEY,
    email             VARCHAR(254) NOT NULL UNIQUE,
    password_hash     VARCHAR(255) NOT NULL,
    name              VARCHAR(150) NOT NULL,
    phone_no          VARCHAR(20)  NOT NULL UNIQUE,
    is_admin          TINYINT(1)   NOT NULL DEFAULT 0,
    is_email_verified TINYINT(1)   NOT NULL DEFAULT 0,
    created_at        DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at        DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    INDEX ix_users_email (email),
    INDEX ix_users_phone_no (phone_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
