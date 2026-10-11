-- ============================================================================
--  PISMO — картинка группы и баны в группах (миграция 25)
-- ============================================================================
--
--  ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. У учётной записи приложения на этом хостинге нет
--  прав ALTER и CREATE, поэтому схему кладёт администратор.
--
--  Пока этого нет: у группы нельзя поставить картинку (меняется только цвет
--  значка), а исключённого из группы тут же добавляют обратно — бан не на чем
--  держать.
--
--      mysql -u root -p --force bdauth < 2026-10-12_group_avatar_bans.sql
--
--  Запускать можно сколько угодно раз.
--
--  То же самое есть в pismo-all.sql — этот файл для тех, кому нужно только
--  это изменение.
-- ============================================================================

USE bdauth;

-- Картинка группы — в самой таблице, как аватар человека и значок сервера.
ALTER TABLE group_chats ADD COLUMN IF NOT EXISTS avatar_data LONGBLOB NULL;

-- Баны в группах. Устроены как server_bans: пара «группа — человек».
CREATE TABLE IF NOT EXISTS group_bans (
  group_id  INT       NOT NULL,
  user_id   INT       NOT NULL,
  banned_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (group_id, user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO schema_migrations (id, name) VALUES
  (25, 'группы: картинка и баны — как у серверов');

SHOW COLUMNS FROM group_chats LIKE 'avatar%';
SELECT id, name, applied_at FROM schema_migrations WHERE id = 25;
