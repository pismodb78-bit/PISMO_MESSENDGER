-- ============================================================
--  PISMO 2.0 — миграции схемы (выполнить ВРУЧНУЮ)
--
--  У приложения нет прав на CREATE/ALTER, поэтому эти изменения
--  накатываются вручную под привилегированным пользователем.
--
--  Как применить:
--    mysql -u root -p bdauth < migrations_2.0.sql
--  или phpMyAdmin: выбрать базу bdauth -> вкладка SQL -> вставить -> Выполнить.
--
--  Скрипт безопасно запускать повторно (CREATE TABLE IF NOT EXISTS).
--  Единственное место, требующее внимания — ALTER friends ADD status:
--  если колонка уже есть, MySQL выдаст ошибку «Duplicate column» —
--  просто пропустите эту строку (или используйте вариант для MariaDB ниже).
-- ============================================================

USE `bdauth`;

-- ── friends.status (заявки в друзья) ────────────────────────
-- Если колонка УЖЕ существует — пропустите этот ALTER.
-- MariaDB 10.2+: можно раскомментировать безопасный вариант и убрать обычный.
ALTER TABLE `friends` ADD COLUMN `status` TINYINT NOT NULL DEFAULT 1;
-- MariaDB: ALTER TABLE `friends` ADD COLUMN IF NOT EXISTS `status` TINYINT NOT NULL DEFAULT 1;

-- ── Приватность ЛС (кто может мне писать) ───────────────────
CREATE TABLE IF NOT EXISTS `user_prefs` (
  `user_id`    INT NOT NULL PRIMARY KEY,
  `dm_privacy` TINYINT NOT NULL DEFAULT 0        -- 0=все, 1=только друзья
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Реакции-эмодзи на сообщения ─────────────────────────────
CREATE TABLE IF NOT EXISTS `message_reactions` (
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое, 2=серверное
  `user_id`    INT NOT NULL,
  `emoji`      VARCHAR(16) NOT NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`message_id`,`scope`,`user_id`,`emoji`),
  KEY `idx_react_msg` (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Закреплённые сообщения ──────────────────────────────────
CREATE TABLE IF NOT EXISTS `pinned_messages` (
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое
  `pinned_by`  INT NOT NULL,
  `pinned_at`  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── История изменений сообщений ─────────────────────────────
CREATE TABLE IF NOT EXISTS `message_edits` (
  `id`         INT NOT NULL AUTO_INCREMENT,
  `message_id` INT NOT NULL,
  `scope`      TINYINT NOT NULL DEFAULT 0,       -- 0=личное, 1=групповое
  `old_text`   TEXT NULL,                         -- прежний текст (зашифрован)
  `edited_at`  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_edits_msg` (`message_id`,`scope`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Журнал миграций (опционально; DbMigrator без DDL-прав его не создаст) ──
CREATE TABLE IF NOT EXISTS `schema_migrations` (
  `id`         INT NOT NULL PRIMARY KEY,
  `name`       VARCHAR(255) NULL,
  `applied_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Отмечаем эти миграции как применённые (чтобы DbMigrator, если у него
-- когда-нибудь появятся права, не пытался пересоздавать):
INSERT IGNORE INTO `schema_migrations` (`id`,`name`) VALUES
  (1,'friends: заявки + status'),
  (2,'user_prefs: приватность ЛС'),
  (3,'users.dm_privacy (запасное хранилище)'),
  (4,'message_reactions'),
  (5,'pinned_messages'),
  (6,'message_edits');
