ALTER TABLE matches
    ADD COLUMN is_active BOOLEAN NOT NULL DEFAULT TRUE;

CREATE INDEX ix_matches_lost_visibility
    ON matches (
        lost_reporter_id,
        is_active,
        status,
        created_at
    );

CREATE INDEX ix_matches_finder_visibility
    ON matches (
        finder_id,
        is_active,
        status,
        created_at
    );