-- Индексы под горячие запросы личных сообщений.
--
-- Зачем: в messages есть только idx_msg_pair (sender_id, receiver_id) и
-- idx_msg_created (created_at). Запросы, которые идут «от получателя»
-- (непрочитанные, отметка прочитанного, список диалогов), опереться на них
-- не могут и сканируют таблицу целиком — вместе с колонками LONGBLOB.
-- Именно это грузит диск на десятки МБ/с при отправке простого текста.
--
-- Выполнять под пользователем с правом ALTER (приложение DDL не делает).
-- Ошибка 1061 (Duplicate key name) при повторном запуске = уже применено.

-- Непрочитанные по отправителю + отметка «прочитано»:
--   WHERE receiver_id=? AND is_read=0
ALTER TABLE `messages`
  ADD INDEX `idx_msg_recv_read` (`receiver_id`, `is_read`, `sender_id`);

-- Список диалогов: ветка «мне писали» — сортировка и агрегат по времени.
ALTER TABLE `messages`
  ADD INDEX `idx_msg_recv_time` (`receiver_id`, `created_at`, `id`);

-- Список диалогов: ветка «я писал».
ALTER TABLE `messages`
  ADD INDEX `idx_msg_send_time` (`sender_id`, `created_at`, `id`);

-- Лента переписки: страница сообщений одного диалога.
ALTER TABLE `messages`
  ADD INDEX `idx_msg_pair_time` (`sender_id`, `receiver_id`, `id`);
