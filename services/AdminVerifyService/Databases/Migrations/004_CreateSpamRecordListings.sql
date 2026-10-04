CREATE TABLE spam_record_listings (
    spam_record_id CHAR(36) NOT NULL,
    listing_id CHAR(36) NOT NULL,
    solve_result ENUM('DELETED', 'SKIPPED', 'FAILED') NULL,
    added_at DATETIME(3) NOT NULL,

    PRIMARY KEY (spam_record_id, listing_id),

    CONSTRAINT uq_spam_record_listings_listing
        UNIQUE (listing_id),

    CONSTRAINT fk_spam_record_listings_record
        FOREIGN KEY (spam_record_id) REFERENCES spam_records(id)
        ON DELETE CASCADE,

    CONSTRAINT fk_spam_record_listings_listing
        FOREIGN KEY (listing_id) REFERENCES tracked_listings(listing_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
