-- Consolidated Item Service migration for Azure's live `item_db` schema.
-- Run manually once AFTER the first deploy (nothing auto-runs in Production -
-- DbInitializer is Development-only). See Sprint2-Deployment-Checklist.md.
--
-- This is the integration of V001-V004, rewritten for the real provisioned
-- schema. The embedded development scripts keep their `item_service` database
-- for local dev; do NOT run those against Azure as-is, or MySQL will create an
-- empty `item_service` database and leave `item_db` with zero tables.
--
-- Idempotent: safe to re-run (CREATE TABLE IF NOT EXISTS). Parent tables come
-- before children so foreign keys resolve.
--
-- Run:
--     Get-Content DevOps_Documentation\azure-item_db-migration.sql |
--       mysql -h lostfound-mysql.mysql.database.azure.com -u <admin> -p item_db

USE item_db;

CREATE TABLE IF NOT EXISTS lost_items (
    id                   CHAR(36)     NOT NULL PRIMARY KEY,
    user_id              CHAR(36)     NOT NULL,
    title                VARCHAR(150) NOT NULL,
    category             VARCHAR(50)  NOT NULL,
    description          VARCHAR(2000) NOT NULL,
    date_lost             DATE         NOT NULL,
    last_known_location  VARCHAR(255) NOT NULL,
    hidden_information   VARCHAR(500) NOT NULL,
    status               VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE',
    created_at           DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at           DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    INDEX ix_lost_items_user_id (user_id),
    INDEX ix_lost_items_status (status),
    INDEX ix_lost_items_date_lost (date_lost)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS lost_item_photos (
    id           CHAR(36)     NOT NULL PRIMARY KEY,
    lost_item_id CHAR(36)     NOT NULL,
    url          VARCHAR(500) NOT NULL,
    created_at   DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    INDEX ix_lost_item_photos_lost_item_id (lost_item_id),
    CONSTRAINT fk_lost_item_photos_lost_item
        FOREIGN KEY (lost_item_id) REFERENCES lost_items (id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS found_items (
    id                   CHAR(36)     NOT NULL PRIMARY KEY,
    user_id              CHAR(36)     NOT NULL,
    title                VARCHAR(150) NOT NULL,
    category             VARCHAR(50)  NOT NULL,
    description          VARCHAR(2000) NOT NULL,
    date_found           DATE         NOT NULL,
    location_found       VARCHAR(255) NOT NULL,
    hidden_information   VARCHAR(500) NOT NULL,
    status               VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE',
    created_at           DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    updated_at           DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
    INDEX ix_found_items_user_id (user_id),
    INDEX ix_found_items_status (status),
    INDEX ix_found_items_date_found (date_found)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS found_item_photos (
    id            CHAR(36)     NOT NULL PRIMARY KEY,
    found_item_id CHAR(36)     NOT NULL,
    url           VARCHAR(500) NOT NULL,
    created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
    INDEX ix_found_item_photos_found_item_id (found_item_id),
    CONSTRAINT fk_found_item_photos_found_item
        FOREIGN KEY (found_item_id) REFERENCES found_items (id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
