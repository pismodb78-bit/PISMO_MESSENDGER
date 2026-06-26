-- ============================================================
--  PISMO — миграция для статусов присутствия (в сети / бездействует / не в сети)
--
--  Выполните этот скрипт ОДИН раз на вашем сервере (phpMyAdmin -> вкладка SQL,
--  выбрав базу bdauth). Он добавляет в таблицу users две колонки:
--    last_seen   — когда клиент последний раз выходил на связь (heartbeat);
--    last_active — когда пользователь последний раз что-то делал (ввод).
--
--  По ним приложение вычисляет статус:
--    • в сети       — heartbeat свежий (<45 c) и была активность (<5 мин);
--    • бездействует  — heartbeat свежий, но активности нет >5 мин;
--    • не в сети     — heartbeat старше 45 c (приложение закрыто).
--
--  Скрипт идемпотентный: повторный запуск не навредит.
-- ============================================================

ALTER TABLE `users`
  ADD COLUMN IF NOT EXISTS `last_seen`   DATETIME NULL DEFAULT NULL,
  ADD COLUMN IF NOT EXISTS `last_active` DATETIME NULL DEFAULT NULL;

-- Если ваша версия MySQL не поддерживает "ADD COLUMN IF NOT EXISTS",
-- закомментируйте блок выше и выполните вместо него (по одной строке,
-- игнорируя ошибку "Duplicate column", если колонка уже есть):
--
-- ALTER TABLE `users` ADD COLUMN `last_seen`   DATETIME NULL DEFAULT NULL;
-- ALTER TABLE `users` ADD COLUMN `last_active` DATETIME NULL DEFAULT NULL;
