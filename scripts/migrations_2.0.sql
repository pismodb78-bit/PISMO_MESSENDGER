-- ============================================================
--  PISMO 2.0 — миграции схемы (выполнить ВРУЧНУЮ)
--
--  У приложения нет прав на CREATE/ALTER, поэтому эти изменения
--  накатываются вручную под привилегированным пользователем.
--
--  Как применить:
--    phpMyAdmin: выбрать базу bdauth -> вкладка SQL -> вставить ВЕСЬ файл -> Выполнить.
--    или: mysql -u root -p bdauth < migrations_2.0.sql
--
--  Скрипт БЕЗОПАСЕН для повторного запуска и НЕ падает, если что-то уже есть
--  (все CREATE TABLE IF NOT EXISTS; friends.status добавляется только если его
--  ещё нет — через проверку, а не «голый» ALTER, который раньше давал #1060).
-- ============================================================

USE `bdauth`;

-- ── Сначала таблицы (идемпотентно, ошибок не будет) ─────────

-- Приватность ЛС (кто может мне писать)
CREATE TABLE IF NOT EXISTS `user_prefs` (
  `user_id`    INT NOT NULL PRIMARY KEY,
  `dm_privacy` TINYINT NOT NULL DEFAULT 0        -- 0=все, 1=только друзья
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Реакции-эмодзи на сообщения
CREATE TABLE IF NOT EXISTS `message_reactions` (
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое, 2=серверное
  `user_id`    INT NOT NULL,
  `emoji`      VARCHAR(16) NOT NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`message_id`,`scope`,`user_id`,`emoji`),
  KEY `idx_react_msg` (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Закреплённые сообщения
CREATE TABLE IF NOT EXISTS `pinned_messages` (
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое
  `pinned_by`  INT NOT NULL,
  `pinned_at`  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- История изменений сообщений
CREATE TABLE IF NOT EXISTS `message_edits` (
  `id`         INT NOT NULL AUTO_INCREMENT,
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое
  `old_text`   TEXT NULL,                         -- прежний текст (зашифрован)
  `edited_at`  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_edits_msg` (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Журнал миграций (опционально)
CREATE TABLE IF NOT EXISTS `schema_migrations` (
  `id`         INT NOT NULL PRIMARY KEY,
  `name`       VARCHAR(255) NULL,
  `applied_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── friends.status: добавляем ТОЛЬКО если колонки ещё нет ───
--  (безопасно для MySQL 5.7+ и MariaDB; повторный запуск не даёт ошибку #1060)
SET @has_status := (
  SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'friends' AND COLUMN_NAME = 'status'
);
SET @sql := IF(@has_status = 0,
  'ALTER TABLE `friends` ADD COLUMN `status` TINYINT NOT NULL DEFAULT 1',
  'DO 0');
PREPARE _stmt FROM @sql;
EXECUTE _stmt;
DEALLOCATE PREPARE _stmt;

-- ── Отметка применённых миграций ────────────────────────────
INSERT IGNORE INTO `schema_migrations` (`id`,`name`) VALUES
  (1,'friends: заявки + status'),
  (2,'user_prefs: приватность ЛС'),
  (3,'users.dm_privacy (запасное хранилище)'),
  (4,'message_reactions'),
  (5,'pinned_messages'),
  (6,'message_edits');
