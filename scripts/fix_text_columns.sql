-- Фикс ошибки «Data too long for column 'text' at row 1».
-- Причина: колонка text была создана как VARCHAR(...), а текст сообщений
-- хранится ЗАШИФРОВАННЫМ (base64 раздувает длину) → короткое сообщение
-- уже не влезает. Расширяем до LONGTEXT (до 4 ГБ) во всех таблицах сообщений.
--
-- Как применить (замените pismo на имя вашей БД, если другое):
--   mysql -u <user> -p pismo < scripts/fix_text_columns.sql
-- либо просто выполните строки ниже в любом SQL-клиенте (HeidiSQL/DBeaver/phpMyAdmin).
-- Данные не теряются, MODIFY только меняет тип колонки.

ALTER TABLE messages        MODIFY text LONGTEXT NOT NULL;
ALTER TABLE group_messages  MODIFY text LONGTEXT NOT NULL;
ALTER TABLE server_messages MODIFY text LONGTEXT NOT NULL;

-- Если какая-то из таблиц позволяет NULL в text и первая команда ругается —
-- используйте вариант без NOT NULL для этой таблицы, например:
-- ALTER TABLE messages MODIFY text LONGTEXT NULL;
