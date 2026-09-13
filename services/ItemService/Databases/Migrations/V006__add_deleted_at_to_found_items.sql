-- V006: add deleted_at to found_items for ItemService
-- Mirrors V005__add_deleted_at_to_lost_items.sql. See that file for rationale.

USE item_service;

ALTER TABLE found_items
    ADD COLUMN deleted_at DATETIME(3) NULL DEFAULT NULL AFTER status;

ALTER TABLE found_items
    ADD INDEX ix_found_items_deleted_at (deleted_at);