ALTER TABLE image_descriptions
    ADD COLUMN lease_token CHAR(36) NULL,
    ADD COLUMN lease_expires_at DATETIME(3) NULL,
    ADD COLUMN model_name VARCHAR(150) NULL;

CREATE INDEX ix_image_descriptions_lease
    ON image_descriptions (processing_status, lease_expires_at);