-- V005: add deleted_at to lost_items for ItemService
-- Idempotent-ish: MySQL has no "ADD COLUMN IF NOT EXISTS" prior to 8.0.29, so this
-- relies on the migration runner's _migrations tracking table to only run once.
--
-- deleted_at IS NULL  => item is live.
-- deleted_at NOT NULL => item is soft-deleted and must never be returned by any
-- public-facing read path (see ItemsController.GetById / LostItemsRepository.GetByIdAsync).

USE item_service;

ALTER TABLE lost_items
    ADD COLUMN deleted_at DATETIME(3) NULL DEFAULT NULL AFTER status;

ALTER TABLE lost_items
    ADD INDEX ix_lost_items_deleted_at (deleted_at);