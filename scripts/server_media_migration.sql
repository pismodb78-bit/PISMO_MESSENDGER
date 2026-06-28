-- Медиа в серверных каналах: вложения, голосовые и видео-кружки.
-- Совместимо с MySQL (в MySQL нет ADD COLUMN IF NOT EXISTS). Безопасно
-- запускать повторно: каждая колонка добавляется только если её ещё нет.

SET @db := DATABASE();

SET @sql := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=@db AND TABLE_NAME='server_messages' AND COLUMN_NAME='image_data')=0,
    'ALTER TABLE server_messages ADD COLUMN image_data LONGBLOB NULL', 'DO 0');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=@db AND TABLE_NAME='server_messages' AND COLUMN_NAME='audio_data')=0,
    'ALTER TABLE server_messages ADD COLUMN audio_data LONGBLOB NULL', 'DO 0');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=@db AND TABLE_NAME='server_messages' AND COLUMN_NAME='video_data')=0,
    'ALTER TABLE server_messages ADD COLUMN video_data LONGBLOB NULL', 'DO 0');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=@db AND TABLE_NAME='server_messages' AND COLUMN_NAME='file_data')=0,
    'ALTER TABLE server_messages ADD COLUMN file_data LONGBLOB NULL', 'DO 0');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA=@db AND TABLE_NAME='server_messages' AND COLUMN_NAME='file_name')=0,
    'ALTER TABLE server_messages ADD COLUMN file_name VARCHAR(255) NULL', 'DO 0');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
