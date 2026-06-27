-- ============================================================
--  PISMO — миграция для серверов (как в Discord)
--  Выполнить ОДИН раз в phpMyAdmin (база bdauth, вкладка SQL).
--  Таблицы сразу под полный функционал (каналы, роли, баны), чтобы
--  не делать повторную миграцию в следующих итерациях.
-- ============================================================

CREATE TABLE IF NOT EXISTS `servers` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `name` VARCHAR(120) NOT NULL,
  `owner_id` INT UNSIGNED NOT NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `server_roles` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `server_id` INT UNSIGNED NOT NULL,
  `name` VARCHAR(80) NOT NULL,
  `color` VARCHAR(9) NOT NULL DEFAULT '#99AAB5',
  `can_ban` TINYINT(1) NOT NULL DEFAULT 0,
  `can_kick` TINYINT(1) NOT NULL DEFAULT 0,
  `can_mute` TINYINT(1) NOT NULL DEFAULT 0,
  `can_manage` TINYINT(1) NOT NULL DEFAULT 0,
  `position` INT NOT NULL DEFAULT 0,
  KEY `idx_srv` (`server_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `server_members` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `server_id` INT UNSIGNED NOT NULL,
  `user_id` INT UNSIGNED NOT NULL,
  `role_id` INT UNSIGNED NULL DEFAULT NULL,
  `muted_notifs` TINYINT(1) NOT NULL DEFAULT 0,
  `joined_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY `uq_member` (`server_id`,`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `server_channels` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `server_id` INT UNSIGNED NOT NULL,
  `name` VARCHAR(120) NOT NULL,
  `type` VARCHAR(10) NOT NULL DEFAULT 'text', -- 'text' | 'voice'
  `position` INT NOT NULL DEFAULT 0,
  KEY `idx_srv` (`server_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `server_messages` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `channel_id` INT UNSIGNED NOT NULL,
  `sender_id` INT UNSIGNED NOT NULL,
  `text` MEDIUMTEXT NULL,
  `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  KEY `idx_ch` (`channel_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS `server_bans` (
  `id` INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  `server_id` INT UNSIGNED NOT NULL,
  `user_id` INT UNSIGNED NOT NULL,
  UNIQUE KEY `uq_ban` (`server_id`,`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
