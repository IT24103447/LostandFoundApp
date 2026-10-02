ALTER TABLE match_appeals
    ADD COLUMN rejection_reason VARCHAR(300) NULL AFTER decided_at;
