CREATE TABLE processed_events (
    event_id CHAR(36) NOT NULL PRIMARY KEY,
    topic VARCHAR(100) NOT NULL,
    processed_at DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
