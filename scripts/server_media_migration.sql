-- Медиа в серверных каналах: вложения, голосовые и видео-кружки.
-- Безопасно выполнять повторно (IF NOT EXISTS, MySQL 8+).
ALTER TABLE server_messages
    ADD COLUMN IF NOT EXISTS image_data LONGBLOB NULL,
    ADD COLUMN IF NOT EXISTS audio_data LONGBLOB NULL,
    ADD COLUMN IF NOT EXISTS video_data LONGBLOB NULL,
    ADD COLUMN IF NOT EXISTS file_data  LONGBLOB NULL,
    ADD COLUMN IF NOT EXISTS file_name  VARCHAR(255) NULL;
