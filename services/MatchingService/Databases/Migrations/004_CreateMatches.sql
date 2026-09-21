CREATE TABLE matches (
    id CHAR(36) NOT NULL PRIMARY KEY,
    lost_item_id CHAR(36) NOT NULL,
    found_item_id CHAR(36) NOT NULL,
    lost_reporter_id CHAR(36) NOT NULL,
    finder_id CHAR(36) NOT NULL,
    claimant_id CHAR(36) NOT NULL,
    claimant_role VARCHAR(10) NOT NULL,
    status VARCHAR(40) NOT NULL,
    confidence_score DECIMAL(5, 2) NOT NULL,
    scoring_version VARCHAR(30) NOT NULL,
    lost_snapshot JSON NOT NULL,
    found_snapshot JSON NOT NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_matches_item_pair
        UNIQUE (lost_item_id, found_item_id),

    INDEX ix_matches_lost_reporter (
        lost_reporter_id,
        created_at
    ),

    INDEX ix_matches_finder (
        finder_id,
        created_at
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;