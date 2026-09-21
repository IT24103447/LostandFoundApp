ALTER TABLE image_descriptions
    ADD COLUMN source_event_type VARCHAR(10) NULL,
    ADD COLUMN source_occurred_at DATETIME(3) NULL,
    ADD COLUMN is_superseded TINYINT(1) NOT NULL DEFAULT 0,
    ADD COLUMN superseded_at DATETIME(3) NULL,
    ADD COLUMN superseded_by_id CHAR(36) NULL;

UPDATE image_descriptions
SET source_event_type = 'CREATED',
    source_occurred_at = created_at
WHERE source_event_type IS NULL
   OR source_occurred_at IS NULL;

ALTER TABLE image_descriptions
    MODIFY COLUMN source_event_type VARCHAR(10) NOT NULL,
    MODIFY COLUMN source_occurred_at DATETIME(3) NOT NULL;

CREATE INDEX ix_image_descriptions_current
    ON image_descriptions
        (item_id, item_type, is_superseded, processing_status, source_occurred_at);