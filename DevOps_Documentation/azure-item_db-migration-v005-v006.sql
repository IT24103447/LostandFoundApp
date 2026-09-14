-- Consolidated Item Service migration V005-V006 for Azure's live `item_db` schema.
-- Run manually BEFORE the deploy that ships the soft-delete feature (LF-68 /
-- "Mark an item as resolved" / My Reports delete) - nothing auto-runs in
-- Production, DbInitializer is Development-only. See Sprint2-Deployment-Checklist.md.
--
-- These are the production equivalents of the embedded development scripts
-- services/ItemService/Databases/Migrations/V005__add_deleted_at_to_lost_items.sql
-- and V006__add_deleted_at_to_found_items.sql, rewritten for the real
-- provisioned schema. Do NOT run the embedded scripts against Azure as-is, or
-- MySQL will create an empty `item_service` database and leave `item_db`
-- untouched.
--
-- What this does:
--   * adds deleted_at DATETIME(3) NULL to lost_items and found_items
--       deleted_at IS NULL  => item is live.
--       deleted_at NOT NULL => item is soft-deleted and must never be returned
--                              by any public-facing read path.
--   * adds a supporting index on each new column.
--   * existing rows keep deleted_at = NULL (live) - no backfill needed.
--
-- Idempotent: safe to re-run. Each ALTER is guarded by an
-- information_schema check so a second run is a no-op instead of an error.
--
-- Run:
--     Get-Content DevOps_Documentation\azure-item_db-migration-v005-v006.sql |
--       mysql -h lostfound-mysql.mysql.database.azure.com -u <admin> -p item_db

USE item_db;

-- ---- lost_items.deleted_at -------------------------------------------------

SET @col_exists := (
    SELECT COUNT(*) FROM information_schema.columns
    WHERE table_schema = 'item_db'
      AND table_name = 'lost_items'
      AND column_name = 'deleted_at'
);

SET @sql := IF(@col_exists = 0,
    'ALTER TABLE lost_items ADD COLUMN deleted_at DATETIME(3) NULL DEFAULT NULL AFTER status',
    'SELECT ''lost_items.deleted_at already exists - skipping''');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @idx_exists := (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema = 'item_db'
      AND table_name = 'lost_items'
      AND index_name = 'ix_lost_items_deleted_at'
);

SET @sql := IF(@idx_exists = 0,
    'ALTER TABLE lost_items ADD INDEX ix_lost_items_deleted_at (deleted_at)',
    'SELECT ''ix_lost_items_deleted_at already exists - skipping''');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ---- found_items.deleted_at ------------------------------------------------

SET @col_exists := (
    SELECT COUNT(*) FROM information_schema.columns
    WHERE table_schema = 'item_db'
      AND table_name = 'found_items'
      AND column_name = 'deleted_at'
);

SET @sql := IF(@col_exists = 0,
    'ALTER TABLE found_items ADD COLUMN deleted_at DATETIME(3) NULL DEFAULT NULL AFTER status',
    'SELECT ''found_items.deleted_at already exists - skipping''');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @idx_exists := (
    SELECT COUNT(*) FROM information_schema.statistics
    WHERE table_schema = 'item_db'
      AND table_name = 'found_items'
      AND index_name = 'ix_found_items_deleted_at'
);

SET @sql := IF(@idx_exists = 0,
    'ALTER TABLE found_items ADD INDEX ix_found_items_deleted_at (deleted_at)',
    'SELECT ''ix_found_items_deleted_at already exists - skipping''');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ---- verification -----------------------------------------------------------

SELECT table_name, column_name, column_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'item_db'
  AND table_name IN ('lost_items', 'found_items')
  AND column_name = 'deleted_at'
ORDER BY table_name;