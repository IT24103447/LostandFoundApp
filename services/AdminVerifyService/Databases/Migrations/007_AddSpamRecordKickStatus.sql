ALTER TABLE spam_records
    ADD COLUMN kick_status ENUM('NOT_REQUESTED', 'PENDING', 'KICKED', 'FAILED')
        NOT NULL DEFAULT 'NOT_REQUESTED'
        AFTER collecting_until;
