CREATE TABLE notification_contacts (
    user_id CHAR(36) NOT NULL PRIMARY KEY,
    email VARCHAR(320) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    last_event_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE match_notification_activation (
    id TINYINT NOT NULL PRIMARY KEY,
    activated_at DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO match_notification_activation (id, activated_at)
VALUES (1, UTC_TIMESTAMP(3));

CREATE TABLE match_notifications (
    id CHAR(36) NOT NULL PRIMARY KEY,
    match_id CHAR(36) NOT NULL,
    recipient_user_id CHAR(36) NOT NULL,
    notification_type VARCHAR(40) NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3) NULL,
    lease_token CHAR(36) NULL,
    lease_expires_at DATETIME(3) NULL,
    sent_at DATETIME(3) NULL,
    error_code VARCHAR(80) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_match_notification
        UNIQUE (match_id, recipient_user_id, notification_type),

    INDEX ix_match_notification_due (
        status,
        next_attempt_at,
        lease_expires_at
    ),

    CONSTRAINT fk_match_notification_match
        FOREIGN KEY (match_id) REFERENCES matches(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE INDEX ix_match_actions_created_at
    ON match_actions (created_at, id);