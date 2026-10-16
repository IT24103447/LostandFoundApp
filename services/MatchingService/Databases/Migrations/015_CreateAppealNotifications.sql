CREATE TABLE appeal_notifications (
    id CHAR(36) NOT NULL PRIMARY KEY,
    appeal_id CHAR(36) NOT NULL,
    notification_type ENUM('APPEAL_VERIFIED', 'APPEAL_REJECTED') NOT NULL,
    recipient_email VARCHAR(320) NOT NULL,
    match_id CHAR(36) NULL,
    status ENUM('PENDING', 'SENT', 'FAILED') NOT NULL DEFAULT 'PENDING',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3) NULL,
    sent_at DATETIME(3) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_appeal_notification
        UNIQUE (appeal_id, notification_type),

    INDEX ix_appeal_notification_due (
        status,
        next_attempt_at
    ),

    CONSTRAINT fk_appeal_notification_appeal
        FOREIGN KEY (appeal_id) REFERENCES match_appeals(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
