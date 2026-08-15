-- =====================================================================
-- PISMO — миграция 14: вместимость голосового канала + подстраховка по
--                      таблице присутствия voice_presence
-- Дата: 2026-08-15
--
-- Зачем: server_channels.user_limit — вместимость голосового канала (как
-- в Discord): 0 = без ограничения, иначе столько человек максимум может
-- находиться в канале одновременно. Приложение рисует пилюлю
-- «занято/лимит» в списке каналов и не пускает в заполненный канал.
--
-- Почему вручную: приложение прогоняет эту миграцию само (DbMigrator,
-- пункт 14), но учётной записи, под которой в БД ходит клиент (user1),
-- не выданы права на CREATE/ALTER — миграция тихо не применяется, и
-- лимит просто не появляется. Этот файл нужно выполнить ОДИН РАЗ под
-- пользователем с правами DDL (root или администратор БД).
--
-- Скрипт ИДЕМПОТЕНТЕН: повторный запуск ничего не сломает и не выдаст
-- ошибку «Duplicate column name» — каждая правка проверяет, нужна ли она.
--
-- Запуск:
--   • phpMyAdmin: слева выбрать базу PISMO (это важно — скрипт опирается
--     на DATABASE()), вкладка SQL, вставить целиком, «Вперёд»;
--   • консоль:  mysql -u root -p <имя_базы> < 2026-08-15_server_channels_user_limit.sql
-- =====================================================================

-- ---------------------------------------------------------------------
-- 0) Журнал миграций. Его создаёт само приложение, но и на это нужны
--    права DDL — поэтому создаём здесь, иначе п.4 не выполнится.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS schema_migrations (
    id         INT NOT NULL PRIMARY KEY,
    name       VARCHAR(255) NULL,
    applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------
-- 1) Таблица присутствия в голосовых каналах. Нужна и для списка «кто в
--    эфире», и для проверки лимита при входе. Если она уже есть —
--    строка ничего не делает.
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS voice_presence (
    channel_id INT NOT NULL,
    user_id    INT NOT NULL,
    joined_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    streaming  TINYINT NOT NULL DEFAULT 0,   -- 1 = включена камера или демонстрация
    mic_muted  TINYINT NOT NULL DEFAULT 0,   -- 1 = микрофон выключен
    deafened   TINYINT NOT NULL DEFAULT 0,   -- 1 = звук полностью заглушён
    PRIMARY KEY (channel_id, user_id),
    INDEX idx_channel (channel_id),
    INDEX idx_seen (last_seen)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------
-- 2) Добор колонок voice_presence, если таблица была создана раньше в
--    урезанном виде (миграция 11 могла не примениться по тем же правам).
--    MySQL не умеет ADD COLUMN IF NOT EXISTS, поэтому собираем запрос
--    условно и выполняем через PREPARE; когда колонка уже есть —
--    выполняется безобидное «DO 0».
-- ---------------------------------------------------------------------
SET @ddl := (SELECT IF(COUNT(*) = 0,
        'ALTER TABLE voice_presence ADD COLUMN streaming TINYINT NOT NULL DEFAULT 0',
        'DO 0')
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'voice_presence' AND COLUMN_NAME = 'streaming');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @ddl := (SELECT IF(COUNT(*) = 0,
        'ALTER TABLE voice_presence ADD COLUMN mic_muted TINYINT NOT NULL DEFAULT 0',
        'DO 0')
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'voice_presence' AND COLUMN_NAME = 'mic_muted');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @ddl := (SELECT IF(COUNT(*) = 0,
        'ALTER TABLE voice_presence ADD COLUMN deafened TINYINT NOT NULL DEFAULT 0',
        'DO 0')
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'voice_presence' AND COLUMN_NAME = 'deafened');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- ---------------------------------------------------------------------
-- 3) ГЛАВНОЕ: вместимость голосового канала. 0 = без ограничения, так что
--    все существующие каналы продолжают работать ровно как раньше.
-- ---------------------------------------------------------------------
SET @ddl := (SELECT IF(COUNT(*) = 0,
        'ALTER TABLE server_channels ADD COLUMN user_limit INT NOT NULL DEFAULT 0',
        'DO 0')
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'server_channels' AND COLUMN_NAME = 'user_limit');
PREPARE stmt FROM @ddl; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- ---------------------------------------------------------------------
-- 4) Отметить миграции применёнными, чтобы приложение не пыталось
--    повторять их при каждом запуске (и не писало ошибку прав в лог).
-- ---------------------------------------------------------------------
INSERT IGNORE INTO schema_migrations (id, name)
VALUES (11, 'voice_presence: значки мьюта микрофона/наушников (как в Discord)'),
       (14, 'server_channels.user_limit: вместимость голосового канала');

-- ---------------------------------------------------------------------
-- 5) Проверка. Должно вернуть строку с user_limit / int / NO / 0.
-- ---------------------------------------------------------------------
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = 'server_channels'
  AND COLUMN_NAME = 'user_limit';
