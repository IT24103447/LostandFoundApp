CREATE TABLE match_appeals (
    id CHAR(36) NOT NULL PRIMARY KEY,
    lost_item_id CHAR(36) NOT NULL,
    found_item_id CHAR(36) NOT NULL,
    lost_reporter_id CHAR(36) NOT NULL,
    finder_id CHAR(36) NOT NULL,
    appellant_id CHAR(36) NOT NULL,
    appellant_role VARCHAR(10) NOT NULL,
    appellant_email VARCHAR(320) NOT NULL,
    appellant_phone VARCHAR(50) NOT NULL,
    score DECIMAL(5, 2) NOT NULL,
    score_breakdown JSON NOT NULL,
    lost_snapshot JSON NOT NULL,
    found_snapshot JSON NOT NULL,
    note VARCHAR(300) NULL,
    status ENUM('PENDING', 'VERIFIED', 'REJECTED') NOT NULL DEFAULT 'PENDING',
    decided_by CHAR(36) NULL,
    decided_at DATETIME(3) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_match_appeals_item_pair
        UNIQUE (lost_item_id, found_item_id),

    INDEX ix_match_appeals_found_item (found_item_id),

    INDEX ix_match_appeals_appellant (
        appellant_id,
        created_at
    ),

    INDEX ix_match_appeals_status (
        status,
        created_at
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
