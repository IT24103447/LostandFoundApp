-- V002: create lost_item_photos table for ItemService
-- One lost item can have zero or more photos (Scenario 2 — optional photos).

USE item_service;

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
