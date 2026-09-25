-- changed during sprint 3 by dev
CREATE TABLE match_resolution_inbox (
    event_id CHAR(36) NOT NULL PRIMARY KEY,
    match_id CHAR(36) NOT NULL,
    lost_item_id CHAR(36) NOT NULL,
    found_item_id CHAR(36) NOT NULL,
    status VARCHAR(20) NOT NULL,
    error_code VARCHAR(80) NULL,
    received_at DATETIME(3) NOT NULL,
    processed_at DATETIME(3) NULL,
    UNIQUE KEY uq_resolution_match (match_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
