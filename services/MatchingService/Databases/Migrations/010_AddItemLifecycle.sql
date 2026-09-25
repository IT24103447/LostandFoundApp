ALTER TABLE matches
    ADD COLUMN deactivated_at DATETIME(3) NULL,
    ADD COLUMN deactivation_reason VARCHAR(40) NULL,
    ADD COLUMN deactivated_item_id CHAR(36) NULL,
    ADD COLUMN deactivated_item_type VARCHAR(10) NULL;

CREATE TABLE matching_item_states (
    item_type VARCHAR(10) NOT NULL,
    item_id CHAR(36) NOT NULL,
    inactive_reason VARCHAR(20) NULL,
    source_event_id CHAR(36) NULL,
    source_occurred_at DATETIME(3) NULL,
    updated_at DATETIME(3) NOT NULL,

    PRIMARY KEY (item_type, item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE matching_item_lifecycle_events (
    event_id CHAR(36) NOT NULL PRIMARY KEY,
    item_type VARCHAR(10) NOT NULL,
    item_id CHAR(36) NOT NULL,
    reason VARCHAR(20) NOT NULL,
    occurred_at DATETIME(3) NOT NULL,
    processed_at DATETIME(3) NOT NULL,

    INDEX ix_matching_lifecycle_item (item_type, item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;