-- Фикс ошибки «Data too long for column 'text' at row 1»
-- (и «#1265 Data truncated for column 'text' at row N» при самом ALTER).
--
-- Причина №1: колонка text была создана как VARCHAR(...), а текст сообщений
--   хранится ЗАШИФРОВАННЫМ (Base64 раздувает длину) → короткое сообщение уже
--   не влезает. Лечится расширением до LONGTEXT.
-- Причина №2 (почему сам ALTER падал с #1265): включён strict-режим MySQL,
--   и предупреждение о конвертации кодировки он превращает в ошибку и обрывает
--   запрос. Данные при этом НЕ теряются — шифр это чистый ASCII (Base64),
--   а старые сообщения — валидный UTF-8. Поэтому просто отключаем strict-режим
--   на время сессии.
--
-- Как применить (в phpMyAdmin вставьте ВСЁ целиком во вкладку SQL и «Вперёд»,
-- либо: mysql -u USER -p bdauth < scripts/fix_text_columns.sql):

SET SESSION sql_mode = '';

ALTER TABLE messages        MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL;
ALTER TABLE group_messages  MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL;
ALTER TABLE server_messages MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL;
