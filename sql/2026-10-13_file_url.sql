-- ============================================================================
--  PISMO — вложения на файловом сервисе (миграция 26)
-- ============================================================================
--
--  ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. У учётной записи приложения на этом хостинге нет
--  прав ALTER, поэтому колонки добавляет администратор.
--
--  Файл теперь лежит на диске сервера (ws-server/files.js), а в сообщении
--  остаётся ссылка на него и размер. Старые сообщения с блобами продолжают
--  открываться по-старому: пустая ссылка означает ровно это.
--
--      mysql -u root -p --force bdauth < 2026-10-13_file_url.sql
--
--  Запускать можно сколько угодно раз.
-- ============================================================================

USE bdauth;

ALTER TABLE messages        ADD COLUMN IF NOT EXISTS file_url  VARCHAR(512) NULL;
ALTER TABLE messages        ADD COLUMN IF NOT EXISTS file_size BIGINT       NULL;
ALTER TABLE group_messages  ADD COLUMN IF NOT EXISTS file_url  VARCHAR(512) NULL;
ALTER TABLE group_messages  ADD COLUMN IF NOT EXISTS file_size BIGINT       NULL;
ALTER TABLE server_messages ADD COLUMN IF NOT EXISTS file_url  VARCHAR(512) NULL;
ALTER TABLE server_messages ADD COLUMN IF NOT EXISTS file_size BIGINT       NULL;

INSERT IGNORE INTO schema_migrations (id, name) VALUES
  (26, 'вложения на файловом сервисе: ссылка вместо блоба');

SHOW COLUMNS FROM messages LIKE 'file_%';
SELECT id, name, applied_at FROM schema_migrations WHERE id = 26;
