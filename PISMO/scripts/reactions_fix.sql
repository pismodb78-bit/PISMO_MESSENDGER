-- PISMO — фикс реакций (выполнить ОДИН раз на «живой» БД bdauth).
-- Проблема: колонка emoji в коллации utf8mb4_general_ci — MySQL считает РАЗНЫЕ
-- эмодзи равными строками, из-за чего второй эмодзи «дубликат» и не ставится
-- (или максимум 2, если какие-то эмодзи всё же различимы). А PRIMARY KEY без
-- emoji вообще ограничивает одной реакцией на пользователя.
--
-- Порядок ВАЖЕН: сперва бинарная коллация (эмодзи становятся байт-различными),
-- потом пересборка первичного ключа с emoji.

-- 1) Бинарная коллация колонки emoji.
ALTER TABLE message_reactions
    MODIFY emoji VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;

-- 2) Пересобрать PRIMARY KEY, чтобы он включал emoji.
--    (Если PK уже (message_id, scope, user_id, emoji) — этот DROP/ADD можно
--    пропустить; при ошибке "Multiple primary key" значит уже всё ок.)
ALTER TABLE message_reactions DROP PRIMARY KEY;
ALTER TABLE message_reactions
    ADD PRIMARY KEY (message_id, scope, user_id, emoji);

-- Проверка: должно показать collation_name = utf8mb4_bin
-- SELECT COLUMN_NAME, COLLATION_NAME FROM information_schema.COLUMNS
--   WHERE TABLE_NAME='message_reactions' AND COLUMN_NAME='emoji';
-- Проверка PK: должно быть 4 колонки, включая emoji
-- SHOW KEYS FROM message_reactions WHERE Key_name='PRIMARY';
