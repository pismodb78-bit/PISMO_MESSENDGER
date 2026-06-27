-- Профиль пользователя: баннер (фон), «о себе», ссылки на соцсети.
-- avatar_data уже добавлен ранее (avatar_migration.sql).
ALTER TABLE users ADD COLUMN banner_data LONGBLOB NULL;
ALTER TABLE users ADD COLUMN about VARCHAR(1000) NULL;
ALTER TABLE users ADD COLUMN social_links VARCHAR(2000) NULL; -- строки "label|url", разделённые \n
