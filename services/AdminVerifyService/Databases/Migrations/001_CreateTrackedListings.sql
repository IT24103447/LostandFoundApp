CREATE TABLE tracked_listings (
    listing_id CHAR(36) NOT NULL PRIMARY KEY,
    listing_type ENUM('LOST', 'FOUND') NOT NULL,
    user_id CHAR(36) NOT NULL,
    posted_at DATETIME(3) NOT NULL,
    created_at DATETIME(3) NOT NULL,

    INDEX ix_tracked_listings_user_posted (
        user_id,
        posted_at
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
