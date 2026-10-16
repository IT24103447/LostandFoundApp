CREATE TABLE spam_records (
    id CHAR(36) NOT NULL PRIMARY KEY,
    user_id CHAR(36) NOT NULL,
    collecting_user_id CHAR(36) NULL,
    score_a INT NOT NULL,
    status ENUM('NEEDS_REVIEW', 'UNDER_REVIEW', 'PENDING_SOLVE', 'SOLVED', 'DISMISSED')
        NOT NULL DEFAULT 'NEEDS_REVIEW',
    collecting_until DATETIME(3) NOT NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_spam_records_collecting_user
        UNIQUE (collecting_user_id),

    INDEX ix_spam_records_status_score (
        status,
        score_a,
        created_at
    ),

    INDEX ix_spam_records_status_created (
        status,
        created_at
    ),

    INDEX ix_spam_records_user (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
