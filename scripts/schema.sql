-- ============================================================
--  PISMO — полная схема БД (реконструирована из кода приложения)
--  MySQL / MariaDB. Безопасно запускать повторно (CREATE TABLE IF NOT EXISTS).
--
--  Импорт:
--    mysql -u root -p < schema.sql
--  или через phpMyAdmin: выбрать базу bdauth -> Импорт -> этот файл.
--
--  Примечание по типам: id — INT AUTO_INCREMENT; медиа — LONGBLOB; текст
--  сообщений хранится в зашифрованном виде (enc:v1:...), поэтому TEXT.
--  Пароли — PBKDF2-хеш (pbkdf2$iter$salt$hash), поэтому VARCHAR(255).
-- ============================================================

CREATE DATABASE IF NOT EXISTS `bdauth`
  DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE `bdauth`;

-- ── Журнал миграций (PISMO 2.0) ─────────────────────────────
-- Приложение само доводит схему до актуальной через DbMigrator; эта таблица
-- фиксирует, какие миграции уже применены (чтобы каждая выполнялась один раз).
CREATE TABLE IF NOT EXISTS `schema_migrations` (
  `id`         INT NOT NULL PRIMARY KEY,
  `name`       VARCHAR(255) NULL,
  `applied_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Пользователи ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `users` (
  `id`           INT NOT NULL AUTO_INCREMENT,
  `login`        VARCHAR(100) NOT NULL,
  `password`     VARCHAR(255) NOT NULL,          -- PBKDF2-хеш
  `Name`         VARCHAR(100) NULL,
  `Surname`      VARCHAR(100) NULL,
  `role`         VARCHAR(20)  NOT NULL DEFAULT 'teacher',  -- admin/teacher/student
  `last_seen`    DATETIME NULL,
  `last_active`  DATETIME NULL,
  `avatar_data`  LONGBLOB NULL,
  `banner_data`  LONGBLOB NULL,
  `about`        TEXT NULL,
  `social_links` TEXT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `uq_users_login` (`login`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Личные сообщения ────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `messages` (
  `id`          INT NOT NULL AUTO_INCREMENT,
  `sender_id`   INT NOT NULL,
  `receiver_id` INT NOT NULL,
  `text`        TEXT NULL,
  `image_data`  LONGBLOB NULL,
  `audio_data`  LONGBLOB NULL,
  `video_data`  LONGBLOB NULL,
  `file_data`   LONGBLOB NULL,
  `file_name`   VARCHAR(255) NULL,
  `is_read`     TINYINT(1) NOT NULL DEFAULT 0,
  `reply_to_id` INT NULL,
  `is_deleted`  TINYINT(1) NOT NULL DEFAULT 0,
  `edited_at`   DATETIME NULL,
  `created_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_msg_pair` (`sender_id`,`receiver_id`),
  KEY `idx_msg_created` (`created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Групповые чаты ──────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `group_chats` (
  `id`         INT NOT NULL AUTO_INCREMENT,
  `name`       VARCHAR(150) NOT NULL,
  `created_by` INT NOT NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `group_members` (
  `group_id` INT NOT NULL,
  `user_id`  INT NOT NULL,
  `is_admin` TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`group_id`,`user_id`),
  KEY `idx_gm_user` (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `group_messages` (
  `id`          INT NOT NULL AUTO_INCREMENT,
  `group_id`    INT NOT NULL,
  `sender_id`   INT NOT NULL,
  `text`        TEXT NULL,
  `image_data`  LONGBLOB NULL,
  `audio_data`  LONGBLOB NULL,
  `video_data`  LONGBLOB NULL,
  `file_data`   LONGBLOB NULL,
  `file_name`   VARCHAR(255) NULL,
  `reply_to_id` INT NULL,
  `is_deleted`  TINYINT(1) NOT NULL DEFAULT 0,
  `edited_at`   DATETIME NULL,
  `created_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_grpmsg_group` (`group_id`),
  KEY `idx_grpmsg_created` (`created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Друзья (заявки: status 0=ожидает, 1=приняты) ────────────
CREATE TABLE IF NOT EXISTS `friends` (
  `user_id`    INT NOT NULL,                -- кто отправил заявку
  `friend_id`  INT NOT NULL,                -- кому отправлена
  `status`     TINYINT NOT NULL DEFAULT 0,  -- 0=заявка, 1=друзья
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`user_id`,`friend_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Настройки пользователя (кто может писать и т.п.) ────────
CREATE TABLE IF NOT EXISTS `user_prefs` (
  `user_id`    INT NOT NULL,
  `dm_privacy` TINYINT NOT NULL DEFAULT 0,  -- 0=писать могут все, 1=только друзья
  PRIMARY KEY (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Блокировки пользователей ────────────────────────────────
CREATE TABLE IF NOT EXISTS `user_blocks` (
  `blocker_id` INT NOT NULL,
  `blocked_id` INT NOT NULL,
  `created_at` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`blocker_id`,`blocked_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Серверы (как в Discord) ─────────────────────────────────
CREATE TABLE IF NOT EXISTS `servers` (
  `id`         INT NOT NULL AUTO_INCREMENT,
  `name`       VARCHAR(150) NOT NULL,
  `owner_id`   INT NOT NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `server_roles` (
  `id`         INT NOT NULL AUTO_INCREMENT,
  `server_id`  INT NOT NULL,
  `name`       VARCHAR(100) NOT NULL,
  `color`      INT NULL,                    -- ARGB (int); NULL = без цвета
  `can_ban`    TINYINT(1) NOT NULL DEFAULT 0,
  `can_kick`   TINYINT(1) NOT NULL DEFAULT 0,
  `can_mute`   TINYINT(1) NOT NULL DEFAULT 0,
  `can_manage` TINYINT(1) NOT NULL DEFAULT 0,
  `position`   INT NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  KEY `idx_roles_server` (`server_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `server_members` (
  `server_id`     INT NOT NULL,
  `user_id`       INT NOT NULL,
  `role_id`       INT NULL,
  `muted_notifs`  TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`server_id`,`user_id`),
  KEY `idx_srvmem_user` (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `server_bans` (
  `server_id` INT NOT NULL,
  `user_id`   INT NOT NULL,
  PRIMARY KEY (`server_id`,`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `server_channels` (
  `id`        INT NOT NULL AUTO_INCREMENT,
  `server_id` INT NOT NULL,
  `name`      VARCHAR(100) NOT NULL,
  `type`      VARCHAR(20) NOT NULL DEFAULT 'text',   -- text / voice
  `position`  INT NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  KEY `idx_chan_server` (`server_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `server_messages` (
  `id`          INT NOT NULL AUTO_INCREMENT,
  `channel_id`  INT NOT NULL,
  `sender_id`   INT NOT NULL,
  `text`        TEXT NULL,
  `image_data`  LONGBLOB NULL,
  `audio_data`  LONGBLOB NULL,
  `video_data`  LONGBLOB NULL,
  `file_data`   LONGBLOB NULL,
  `file_name`   VARCHAR(255) NULL,
  `reply_to_id` INT NULL,
  `is_deleted`  TINYINT(1) NOT NULL DEFAULT 0,
  `edited_at`   DATETIME NULL,
  `created_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_srvmsg_channel` (`channel_id`),
  KEY `idx_srvmsg_created` (`created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Звонки ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS `call_sessions` (
  `id`          INT NOT NULL AUTO_INCREMENT,
  `caller_id`   INT NOT NULL,
  `callee_id`   INT NULL,
  `group_id`    INT NULL,
  `status`      VARCHAR(20) NOT NULL DEFAULT 'ringing',  -- ringing/active/ended
  `has_video`   TINYINT(1) NOT NULL DEFAULT 0,
  `created_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `answered_at` DATETIME NULL,
  `ended_at`    DATETIME NULL,
  PRIMARY KEY (`id`),
  KEY `idx_call_status` (`status`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `call_participants` (
  `id`        INT NOT NULL AUTO_INCREMENT,
  `call_id`   INT NOT NULL,
  `user_id`   INT NOT NULL,
  `joined_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `left_at`   DATETIME NULL,
  PRIMARY KEY (`id`),
  KEY `idx_cp_call` (`call_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Присутствие в голосовых каналах серверов ────────────────
CREATE TABLE IF NOT EXISTS `voice_presence` (
  `channel_id` INT NOT NULL,
  `user_id`    INT NOT NULL,
  `joined_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `last_seen`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `streaming`  TINYINT(1) NOT NULL DEFAULT 0,
  `mic_muted`  TINYINT(1) NOT NULL DEFAULT 0,
  `deafened`   TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`channel_id`,`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ── Начальный админ (пароль задать через приложение/сброс) ──
-- INSERT IGNORE INTO users (login, password, Name, Surname, role)
--   VALUES ('admin', 'admin', 'Admin', '', 'admin');
-- (после первого входа пароль автоматически перехешируется в PBKDF2)
