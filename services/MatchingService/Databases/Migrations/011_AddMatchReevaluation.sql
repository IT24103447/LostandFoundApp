CREATE TABLE matching_item_snapshots (
    item_type VARCHAR(10) NOT NULL,
    item_id CHAR(36) NOT NULL,
    source_event_id CHAR(36) NOT NULL,
    source_event_type VARCHAR(10) NOT NULL,
    source_occurred_at DATETIME(6) NOT NULL,
    snapshot_json JSON NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    PRIMARY KEY (item_type, item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE match_reevaluation_jobs (
    event_id CHAR(36) NOT NULL PRIMARY KEY,
    item_type VARCHAR(10) NOT NULL,
    item_id CHAR(36) NOT NULL,
    status VARCHAR(15) NOT NULL DEFAULT 'PENDING',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3) NULL,
    lease_token CHAR(36) NULL,
    lease_expires_at DATETIME(3) NULL,
    error_code VARCHAR(60) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    INDEX ix_match_reevaluation_due (
        status,
        next_attempt_at,
        created_at
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE INDEX ix_matches_reevaluation_lost
    ON matches (lost_item_id, is_active, status);

CREATE INDEX ix_matches_reevaluation_found
    ON matches (found_item_id, is_active, status);