-- Присутствие в голосовых каналах серверов (кто «в эфире» прямо сейчас).
-- Клиент пишет heartbeat пока открыта форма звонка голосового канала,
-- ServersForm читает эту таблицу и показывает участников под каналом.
CREATE TABLE IF NOT EXISTS voice_presence (
    channel_id INT NOT NULL,
    user_id    INT NOT NULL,
    joined_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    streaming  TINYINT NOT NULL DEFAULT 0,  -- 1 = включена камера или демонстрация экрана
    PRIMARY KEY (channel_id, user_id),
    INDEX idx_channel (channel_id),
    INDEX idx_seen (last_seen)
);

-- Если таблица уже существовала без колонки streaming — добавить вручную:
-- ALTER TABLE voice_presence ADD COLUMN streaming TINYINT NOT NULL DEFAULT 0;
