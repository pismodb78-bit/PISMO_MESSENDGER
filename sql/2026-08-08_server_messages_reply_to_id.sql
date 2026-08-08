-- =====================================================================
-- PISMO — миграция: server_messages.reply_to_id
-- Дата: 2026-08-08
--
-- Зачем: включает ответы в каналах серверов и позволяет считать красную
-- цифру «ответ на моё сообщение» на сервере/канале. В текущей БД колонки
-- нет, из-за чего ответы не сохранялись, а запрос бейджей падал.
--
-- Безопасно запускать повторно: колонка/индекс добавляются только если
-- их ещё нет (проверка через information_schema, без ошибок при повторе).
-- Работает и на MySQL, и на MariaDB.
--
-- Запуск:  mysql -u <user> -p <db_name> < 2026-08-08_server_messages_reply_to_id.sql
-- =====================================================================

-- ── 1) Колонка reply_to_id ───────────────────────────────────────────
SET @col := (
    SELECT COUNT(*) FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME   = 'server_messages'
      AND COLUMN_NAME  = 'reply_to_id'
);
SET @sql := IF(@col = 0,
    'ALTER TABLE server_messages ADD COLUMN reply_to_id INT UNSIGNED NULL',
    'SELECT ''reply_to_id уже существует — пропуск'' AS info');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ── 2) Индекс для быстрого поиска ответов ─────────────────────────────
SET @idx := (
    SELECT COUNT(*) FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME   = 'server_messages'
      AND INDEX_NAME   = 'idx_reply'
);
SET @sql := IF(@idx = 0,
    'ALTER TABLE server_messages ADD KEY idx_reply (reply_to_id)',
    'SELECT ''idx_reply уже существует — пропуск'' AS info');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ── 3) Отметить миграцию применённой (чтобы приложение её не повторяло) ─
CREATE TABLE IF NOT EXISTS schema_migrations (
    id         INT NOT NULL PRIMARY KEY,
    name       VARCHAR(255) NULL,
    applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO schema_migrations (id, name)
VALUES (13, 'server_messages.reply_to_id: ответы в каналах серверов');
