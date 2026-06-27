-- Полноценные ответы (reply) в каналах серверов: ссылка на исходное сообщение.
ALTER TABLE server_messages ADD COLUMN reply_to_id INT NULL;
