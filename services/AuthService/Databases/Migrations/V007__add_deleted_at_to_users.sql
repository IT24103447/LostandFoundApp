USE auth_service;

SET @col_exists := (
    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'auth_service'
      AND TABLE_NAME = 'users'
      AND COLUMN_NAME = 'deleted_at'
);
SET @sql := IF(@col_exists = 0,
    'ALTER TABLE users ADD COLUMN deleted_at DATETIME(3) NULL DEFAULT NULL',
    'SELECT 1');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
