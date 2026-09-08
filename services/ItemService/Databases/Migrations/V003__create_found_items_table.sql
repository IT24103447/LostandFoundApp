-- V003: create found_items table for ItemService
-- Idempotent: safe to re-run. Applied dev-only via DbInitializer in Development.
--
-- hidden_information is intentionally NOT part of any DTO returned to the frontend
-- (see Models/Dtos/FoundItemResponseDto.cs and FoundItemsController) — it exists purely
-- as a verification signal for the Matching Service, consumed off the Kafka create event.

USE item_service;

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
