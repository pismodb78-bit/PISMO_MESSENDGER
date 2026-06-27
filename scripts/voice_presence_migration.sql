-- Присутствие в голосовых каналах серверов (кто «в эфире» прямо сейчас).
-- Клиент пишет heartbeat пока открыта форма звонка голосового канала,
-- ServersForm читает эту таблицу и показывает участников под каналом.
CREATE TABLE IF NOT EXISTS voice_presence (
    channel_id INT NOT NULL,
    user_id    INT NOT NULL,
    joined_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (channel_id, user_id),
    INDEX idx_channel (channel_id),
    INDEX idx_seen (last_seen)
);
