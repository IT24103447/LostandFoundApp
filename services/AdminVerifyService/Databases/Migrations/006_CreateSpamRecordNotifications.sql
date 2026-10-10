CREATE TABLE spam_record_notifications (
    id CHAR(36) NOT NULL PRIMARY KEY,
    spam_record_id CHAR(36) NOT NULL,
    recipient_email VARCHAR(320) NOT NULL,
    status ENUM('PENDING', 'SENT', 'FAILED') NOT NULL DEFAULT 'PENDING',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(3) NULL,
    sent_at DATETIME(3) NULL,
    created_at DATETIME(3) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    CONSTRAINT uq_spam_record_notification
        UNIQUE (spam_record_id, recipient_email),

    INDEX ix_spam_record_notification_due (
        status,
        next_attempt_at
    ),

    CONSTRAINT fk_spam_record_notification_record
        FOREIGN KEY (spam_record_id) REFERENCES spam_records(id)
        ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
