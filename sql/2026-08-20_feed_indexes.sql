-- Индексы лент: личные сообщения, группы, каналы серверов.
--
-- Зачем. Запросы, которые идут ОТ ПОЛУЧАТЕЛЯ, опереться на существующие
-- idx_msg_pair (sender_id, receiver_id) и idx_msg_created (created_at) не
-- могут и сканируют messages ЦЕЛИКОМ:
--
--   * непрочитанные по отправителям  — WHERE receiver_id=? AND is_read=0
--   * список диалогов, ветка «мне писали»
--   * отметка «прочитано»
--
-- Первый из них уходит на каждый тик опроса у КАЖДОГО клиента и вдобавок на
-- каждое событие «новое сообщение». Отсюда полка чтения в сотню мегабайт на
-- десяток секунд после каждого отправленного сообщения — при том, что сам
-- текст весит сотню байт.
--
-- Приложение пробует создать эти индексы само (DbMigrator, миграции 17 и 18).
-- Этот файл нужен, если у учётной записи приложения нет права ALTER.
-- Ошибка 1061 (Duplicate key name) при повторном запуске = уже применено.

ALTER TABLE `messages`
  ADD INDEX `idx_msg_recv_read` (`receiver_id`, `is_read`, `sender_id`);
ALTER TABLE `messages`
  ADD INDEX `idx_msg_recv_time` (`receiver_id`, `created_at`, `id`);
ALTER TABLE `messages`
  ADD INDEX `idx_msg_send_time` (`sender_id`, `created_at`, `id`);
ALTER TABLE `messages`
  ADD INDEX `idx_msg_pair_time` (`sender_id`, `receiver_id`, `id`);

-- Группы и каналы: опрос спрашивает «есть ли новое» максимальным id, лента
-- берёт последнюю страницу. И то и другое должно быть движением к концу
-- индекса, а не проходом по всей истории — строки там широкие, с вложениями.
ALTER TABLE `group_messages`
  ADD INDEX `idx_gm_group_id` (`group_id`, `id`);
ALTER TABLE `server_messages`
  ADD INDEX `idx_sm_channel_id` (`channel_id`, `id`);
