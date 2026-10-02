CREATE TABLE matching_item_hidden_information (
    item_type VARCHAR(10) NOT NULL,
    item_id CHAR(36) NOT NULL,
    hidden_information TEXT NOT NULL,
    source_occurred_at DATETIME(6) NOT NULL,
    updated_at DATETIME(3) NOT NULL,

    PRIMARY KEY (item_type, item_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
