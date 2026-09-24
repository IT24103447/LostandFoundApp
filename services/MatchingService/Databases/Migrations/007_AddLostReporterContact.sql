ALTER TABLE matches
    ADD COLUMN lost_reporter_email VARCHAR(320) NULL,
    ADD COLUMN lost_reporter_phone VARCHAR(50) NULL;