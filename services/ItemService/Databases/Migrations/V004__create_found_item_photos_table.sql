-- V004: create found_item_photos table for ItemService
-- One found item can have zero or more photos (Scenario 2 — optional photo).

USE item_service;

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
