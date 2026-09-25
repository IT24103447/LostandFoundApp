ALTER TABLE matches
    ADD COLUMN finder_email VARCHAR(320) NULL,
    ADD COLUMN finder_phone VARCHAR(50) NULL;

CREATE TABLE match_actions (
    id CHAR(36) NOT NULL PRIMARY KEY,
    match_id CHAR(36) NOT NULL,
    actor_user_id CHAR(36) NOT NULL,
    actor_role VARCHAR(10) NOT NULL,
    action VARCHAR(10) NOT NULL,
    previous_status VARCHAR(40) NOT NULL,
    new_status VARCHAR(40) NOT NULL,
    created_at DATETIME(3) NOT NULL,

    INDEX ix_match_actions_match (match_id, created_at),

    CONSTRAINT fk_match_actions_match
        FOREIGN KEY (match_id) REFERENCES matches(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;