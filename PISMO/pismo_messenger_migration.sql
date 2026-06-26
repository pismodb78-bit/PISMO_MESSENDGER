-- ============================================================
--  PISMO — дополнение к bdauth
--  Запускать в phpMyAdmin на базе bdauth
-- ============================================================

-- Таблица личных сообщений
CREATE TABLE IF NOT EXISTS `messages` (
  `id`          INT(11) UNSIGNED NOT NULL AUTO_INCREMENT,
  `sender_id`   INT(10) UNSIGNED NOT NULL,
  `receiver_id` INT(10) UNSIGNED NOT NULL,
  `text`        TEXT             NOT NULL DEFAULT '',
  `image_data`  LONGBLOB                  DEFAULT NULL,
  `is_read`     TINYINT(1)       NOT NULL DEFAULT 0,
  `created_at`  DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_sender`   (`sender_id`),
  KEY `idx_receiver` (`receiver_id`),
  KEY `idx_conv`     (`sender_id`, `receiver_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Убедиться что у users есть PRIMARY KEY (в дампе его не было, только UNIQUE)
-- Если уже есть — эта строка упадёт с ошибкой, просто пропустите её
ALTER TABLE `users` ADD PRIMARY KEY (`id`);
