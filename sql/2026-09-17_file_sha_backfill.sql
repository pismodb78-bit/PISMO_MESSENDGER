-- ============================================================================
--  Дозаполнение отпечатков у файлов, залитых ДО появления столбца file_sha
-- ============================================================================
--
--  ЗАЧЕМ. Отпечаток проставляется в момент заливки, поэтому у всего, что уже
--  лежало в базе, он пустой. Для таких файлов дедупликация не работает: донора
--  среди них не найти, и тот же файл второму собеседнику уходит заново. Этот
--  файл считает отпечатки задним числом — после него старые вложения тоже
--  становятся донорами, а повторная отправка любого из них идёт мгновенно.
--
--  ПОЧЕМУ ЭТО ДЕЛАЕТСЯ ЗАПРОСОМ, А НЕ ПРИЛОЖЕНИЕМ. Отпечаток считает сам
--  сервер, функцией SHA2 — байты никуда не едут. Если бы это делал клиент, ему
--  пришлось бы СКАЧАТЬ каждое вложение целиком и залить обратно одно число:
--  гигабайты трафика ради нескольких сотен строк.
--
--  ЧЕГО ЭТО СТОИТ. Чтобы посчитать отпечаток, сервер разворачивает вложение в
--  памяти. Файл на двести мегабайт — это двести мегабайт разом, а памяти на
--  этой машине два гигабайта. Поэтому работа разбита на порции и на два
--  прохода: сначала мелочь пачками, потом крупное по одному. Делайте это
--  ночью или когда никто не сидит в переписке.
--
--  ЗАПУСКАТЬ МОЖНО СКОЛЬКО УГОДНО РАЗ и прерывать в любой момент: уже
--  посчитанное второй раз не считается, недоделанное доделается в следующий.
-- ============================================================================

USE bdauth;


-- ── Сколько работы ──────────────────────────────────────────────────────────
-- Выполните сначала это. Если в обоих столбцах нули — делать нечего.
SELECT 'messages' AS t,
       COUNT(*)                                   AS всего_без_отпечатка,
       SUM(OCTET_LENGTH(file_data) > 33554432)    AS из_них_крупных
  FROM messages        WHERE file_sha IS NULL AND file_data IS NOT NULL
UNION ALL
SELECT 'group_messages',
       COUNT(*), SUM(OCTET_LENGTH(file_data) > 33554432)
  FROM group_messages  WHERE file_sha IS NULL AND file_data IS NOT NULL
UNION ALL
SELECT 'server_messages',
       COUNT(*), SUM(OCTET_LENGTH(file_data) > 33554432)
  FROM server_messages WHERE file_sha IS NULL AND file_data IS NOT NULL;


-- ── Проход 1: файлы до 32 МБ, пачками по сто ────────────────────────────────
--
-- Повторяйте эти три запроса, пока каждый не ответит «0 rows affected».
-- Так работа идёт короткими шагами: её видно, её можно прервать, и она не
-- держит блокировку на всю таблицу.

UPDATE messages        SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL
   AND OCTET_LENGTH(file_data) <= 33554432
 LIMIT 100;

UPDATE group_messages  SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL
   AND OCTET_LENGTH(file_data) <= 33554432
 LIMIT 100;

UPDATE server_messages SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL
   AND OCTET_LENGTH(file_data) <= 33554432
 LIMIT 100;


-- ── Проход 2: крупные файлы, по одному ──────────────────────────────────────
--
-- Тоже повторяйте, пока не ответит «0 rows affected». По одному — намеренно:
-- каждая такая строка разворачивается в памяти целиком, и две сразу могут
-- упереться в её предел.

UPDATE messages        SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL LIMIT 1;

UPDATE group_messages  SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL LIMIT 1;

UPDATE server_messages SET file_sha = SHA2(file_data, 256)
 WHERE file_sha IS NULL AND file_data IS NOT NULL LIMIT 1;


-- ── Что получилось ──────────────────────────────────────────────────────────
--
-- «Без отпечатка» должно стать нулём во всех трёх строках. «Повторов» — это
-- сколько вложений лежит в базе больше одного раза: именно столько заливок
-- дедупликация теперь и сэкономит на будущих пересылках.
SELECT 'messages' AS t,
       SUM(file_sha IS NULL AND file_data IS NOT NULL) AS без_отпечатка,
       COUNT(DISTINCT file_sha)                        AS разных_файлов,
       SUM(file_sha IS NOT NULL)                       AS всего_вложений
  FROM messages
UNION ALL
SELECT 'group_messages',
       SUM(file_sha IS NULL AND file_data IS NOT NULL),
       COUNT(DISTINCT file_sha), SUM(file_sha IS NOT NULL)
  FROM group_messages
UNION ALL
SELECT 'server_messages',
       SUM(file_sha IS NULL AND file_data IS NOT NULL),
       COUNT(DISTINCT file_sha), SUM(file_sha IS NOT NULL)
  FROM server_messages;
