CREATE TABLE password_reset_tokens (
    id          CHAR(36)     PRIMARY KEY,
    user_id     CHAR(36)     NOT NULL,
    code_hash   CHAR(64)     NOT NULL UNIQUE,
    expires_at  DATETIME(3)  NOT NULL,
    attempts    INT          DEFAULT 0,
    used_at     DATETIME(3)  NULL,
    created_at  DATETIME(3)  DEFAULT CURRENT_TIMESTAMP(3),

    INDEX idx_prt_user_id  (user_id),
    INDEX idx_prt_expires  (expires_at)
);
