-- ============================================================================
--  PISMO — всё, что нужно базе, одним файлом
-- ============================================================================
--
--  ЗАЧЕМ ЭТОТ ФАЙЛ. Обычно схему правят сами приложения при запуске
--  (DbMigrator: версионированные миграции, журнал schema_migrations, общий у
--  ПК и телефона). Но у учётной записи приложения может не быть прав ALTER и
--  CREATE — на этом хостинге так и есть. Тогда те же изменения кладёт
--  администратор, отсюда.
--
--  ЗАПУСКАТЬ МОЖНО СКОЛЬКО УГОДНО РАЗ. База — MariaDB, а она понимает
--  IF NOT EXISTS и у ALTER TABLE, поэтому повторный запуск ничего не сломает
--  и не потеряет: уже существующее просто пропускается.
--
--      mysql -u root -p --force bdauth < pismo-all.sql
--
--  --force ЗДЕСЬ НУЖЕН. Не для того, чтобы прятать ошибки: часть строк
--  обращается к таблицам, которых на конкретной установке может не быть
--  (например voice_presence, если голосовыми каналами не пользовались). Без
--  --force первая же такая строка оборвала бы весь файл, и всё после неё не
--  выполнилось бы. С ним такие строки просто пропускаются, а в конце файл сам
--  показывает, что получилось.
--
--  В конце файл отмечает миграции выполненными в журнале. Это нужно, чтобы
--  приложения не пытались повторить то, что уже сделано, и не спотыкались об
--  отсутствие прав при каждом запуске.
--
--  Здесь НЕТ создания основных таблиц (users, messages, group_messages,
--  server_*, voice_presence): они старше журнала миграций и приезжают с дампом
--  базы. Файл добавляет только то, что появилось позже.
-- ============================================================================


-- ── Журнал миграций ─────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS schema_migrations (
  id         INT          NOT NULL PRIMARY KEY,
  name       VARCHAR(255) NULL,
  applied_at TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ── 1–6, 8. Таблицы, появившиеся после исходного дампа ──────────────────────
CREATE TABLE IF NOT EXISTS friends (
  user_id    INT       NOT NULL,
  friend_id  INT       NOT NULL,
  status     TINYINT   NOT NULL DEFAULT 0,
  created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id, friend_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Старая friends могла быть без status: там всё, что есть, — уже друзья.
ALTER TABLE friends ADD COLUMN IF NOT EXISTS status TINYINT NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS user_prefs (
  user_id    INT     NOT NULL PRIMARY KEY,
  dm_privacy TINYINT NOT NULL DEFAULT 0
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Запасное хранилище той же настройки — на случай, если user_prefs недоступна.
ALTER TABLE users ADD COLUMN IF NOT EXISTS dm_privacy TINYINT NOT NULL DEFAULT 0;

-- Реакции. Коллация БИНАРНАЯ, и это не придирка: в utf8mb4_general_ci разные
-- эмодзи сравниваются как РАВНЫЕ, из-за чего вторая реакция удаляла первую, а
-- первичный ключ считал их дубликатом.
CREATE TABLE IF NOT EXISTS message_reactions (
  message_id INT         NOT NULL,
  scope      TINYINT     NOT NULL DEFAULT 0,
  user_id    INT         NOT NULL,
  emoji      VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
  created_at TIMESTAMP   NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (message_id, scope, user_id, emoji),
  KEY idx_react_msg (message_id, scope)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
-- Если таблица досталась со старой коллацией — приводим. MODIFY данные не
-- трогает и повтора не боится.
ALTER TABLE message_reactions
  MODIFY emoji VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL;

CREATE TABLE IF NOT EXISTS pinned_messages (
  message_id INT       NOT NULL,
  scope      TINYINT   NOT NULL DEFAULT 0,
  pinned_by  INT       NOT NULL,
  pinned_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (message_id, scope)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS message_edits (
  id         INT       NOT NULL AUTO_INCREMENT,
  message_id INT       NOT NULL,
  scope      TINYINT   NOT NULL DEFAULT 0,
  old_text   TEXT      NULL,
  edited_at  TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  KEY idx_edits_msg (message_id, scope)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS server_reads (
  user_id      INT       NOT NULL,
  channel_id   INT       NOT NULL,
  last_read_id INT       NOT NULL DEFAULT 0,
  read_at      TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id, channel_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ── 11. Значки мьюта в голосовом канале ─────────────────────────────────────
ALTER TABLE voice_presence ADD COLUMN IF NOT EXISTS mic_muted TINYINT NOT NULL DEFAULT 0;
ALTER TABLE voice_presence ADD COLUMN IF NOT EXISTS deafened  TINYINT NOT NULL DEFAULT 0;


-- ── 12. Текст сообщений → LONGTEXT ──────────────────────────────────────────
--
-- На части установок колонка text была VARCHAR, и длинное сообщение или
-- длинная подпись к файлу падали с «Data too long for column 'text'».
--
-- strict-режим превращает предупреждение о смене кодировки (#1265) в ошибку и
-- обрывает ALTER — поэтому на время снимаем его для этой сессии. Данные не
-- теряются: шифр это Base64 (ASCII), старый текст — валидный UTF-8.
--
-- Колонка объявляется NULL, а не NOT NULL: снять ограничение можно всегда, а
-- поставить — только если ни в одной строке нет пустого значения, и на живой
-- базе это иногда обрывалось. Приложение с NULL в тексте работает — у него в
-- этой же миграции ровно такой запасной путь.
SET SESSION sql_mode = '';
ALTER TABLE messages        MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NULL;
ALTER TABLE group_messages  MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NULL;
ALTER TABLE server_messages MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NULL;


-- ── 13. Ответы в каналах серверов ───────────────────────────────────────────
ALTER TABLE server_messages ADD COLUMN IF NOT EXISTS reply_to_id INT UNSIGNED NULL;
ALTER TABLE server_messages ADD INDEX  IF NOT EXISTS idx_reply (reply_to_id);


-- ── 14. Вместимость голосового канала ───────────────────────────────────────
ALTER TABLE server_channels ADD COLUMN IF NOT EXISTS user_limit INT NOT NULL DEFAULT 0;


-- ── 15. Упоминания в каналах ────────────────────────────────────────────────
--
-- Текст сообщений в базе зашифрован, разобрать упоминания запросом нельзя —
-- поэтому они лежат отдельной таблицей, которую пишет клиент при отправке.
CREATE TABLE IF NOT EXISTS server_mentions (
  message_id BIGINT NOT NULL,
  channel_id INT    NOT NULL,
  user_id    INT    NOT NULL,
  PRIMARY KEY (message_id, user_id),
  KEY idx_mention_lookup (user_id, channel_id, message_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ── 16. Отдельное право на управление каналами ──────────────────────────────
ALTER TABLE server_roles ADD COLUMN IF NOT EXISTS can_channels TINYINT(1) NOT NULL DEFAULT 0;
-- Кто уже мог управлять сервером — может и каналами. Выполняется один раз:
-- повторный запуск просто выставит те же единицы тем же ролям.
UPDATE server_roles SET can_channels = 1 WHERE can_manage = 1;


-- ── 17. Индексы под ленту сообщений ─────────────────────────────────────────
--
-- ЭТО ЛЕЧИТ «СОТНЮ МЕГАБАЙТ ЧТЕНИЯ НА КАЖДОЕ СООБЩЕНИЕ». Запрос вида
--   WHERE receiver_id=? AND is_read=0 [AND sender_id=?]
-- по индексу только на receiver_id находит все входящие, но is_read и
-- sender_id в индексе нет — и за каждым письмом идёт поход в саму таблицу. А
-- строка там широкая: longtext с текстом и четыре longblob. Запрос уходит на
-- каждый опрос списка чатов у КАЖДОГО клиента, то есть раз в две с половиной
-- секунды. Индекс ниже закрывает его целиком, не заглядывая в таблицу.
ALTER TABLE messages ADD INDEX IF NOT EXISTS idx_msg_recv_read (receiver_id, is_read, sender_id);
ALTER TABLE messages ADD INDEX IF NOT EXISTS idx_msg_recv_time (receiver_id, created_at, id);
ALTER TABLE messages ADD INDEX IF NOT EXISTS idx_msg_send_time (sender_id, created_at, id);
ALTER TABLE messages ADD INDEX IF NOT EXISTS idx_msg_pair_time (sender_id, receiver_id, id);


-- ── 19. Закреплённые ЧАТЫ, общие для ПК и телефона ──────────────────────────
--
-- Раньше каждый клиент хранил список закреплённых диалогов у себя: на ПК
-- файлом, на телефоне — в настройках приложения. Из-за этого один и тот же
-- человек видел РАЗНЫЙ порядок списка на компьютере и в телефоне.
--
-- scope оставлен на будущее с теми же значениями, что у закреплённых
-- сообщений: 0 — личный чат, 1 — групповой. Сейчас пишется только 0.
CREATE TABLE IF NOT EXISTS chat_pins (
  user_id   INT       NOT NULL,
  scope     TINYINT   NOT NULL DEFAULT 0,
  target_id INT       NOT NULL,
  pinned_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (user_id, scope, target_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;


-- ── 20. Отпечаток вложения: один файл не заливается дважды ──────────────────
--
-- Отправить одно видео двум собеседникам значило залить его на сервер два
-- раза — второй раз ровно так же долго, как первый, хотя те же байты там уже
-- лежат. Отпечаток позволяет узнать СВОЁ уже залитое вложение и скопировать
-- его внутри базы (INSERT … SELECT): по сети не уходит ни байта. Он же
-- избавляет от повторного СКАЧИВАНИЯ того же файла в другом чате.
--
-- Донора ищут только среди своих отправленных — отсюда индекс по паре
-- (отправитель, отпечаток), а не по одному отпечатку.
ALTER TABLE messages        ADD COLUMN IF NOT EXISTS file_sha CHAR(64) NULL;
ALTER TABLE group_messages  ADD COLUMN IF NOT EXISTS file_sha CHAR(64) NULL;
ALTER TABLE server_messages ADD COLUMN IF NOT EXISTS file_sha CHAR(64) NULL;

ALTER TABLE messages        ADD INDEX IF NOT EXISTS idx_file_sha (sender_id, file_sha);
ALTER TABLE group_messages  ADD INDEX IF NOT EXISTS idx_file_sha (sender_id, file_sha);
ALTER TABLE server_messages ADD INDEX IF NOT EXISTS idx_file_sha (sender_id, file_sha);


-- ── Отметки в журнале ───────────────────────────────────────────────────────
--
-- Только то, что действительно сделано ВЫШЕ.
--
-- Здесь НЕТ номеров 7, 10 и 18, и это намеренно:
--   7 и 10 — пересборка первичного ключа реакций. Одним запросом её безопасно
--            не написать: DROP PRIMARY KEY падает там, где ключа нет, а нужное
--            состояние зависит от того, что досталось от прошлой схемы.
--            Приложение делает это само, с проверками, и прав ему хватает.
--   18     — разовая чистка двойных пометок пересылки. SQL-ом она невозможна:
--            текст в базе зашифрован, ни LIKE, ни REPLACE до пометки не
--            доберутся. Строки читает, расшифровывает и чинит само приложение.
INSERT IGNORE INTO schema_migrations (id, name) VALUES
  (1,  'friends: заявки + status'),
  (2,  'user_prefs: приватность ЛС'),
  (3,  'users.dm_privacy (запасное хранилище)'),
  (4,  'message_reactions: реакции на сообщения'),
  (5,  'pinned_messages: закреплённые сообщения'),
  (6,  'message_edits: история изменений сообщений'),
  (8,  'server_reads: метки прочитанного в каналах серверов'),
  (9,  'message_reactions.emoji: бинарная коллация'),
  (11, 'voice_presence: значки мьюта микрофона/наушников'),
  (12, 'text-колонки сообщений → LONGTEXT'),
  (13, 'server_messages.reply_to_id: ответы в каналах серверов'),
  (14, 'server_channels.user_limit: вместимость голосового канала'),
  (15, 'server_mentions: упоминания в каналах (текст в БД зашифрован)'),
  (16, 'server_roles.can_channels: отдельное право на каналы'),
  (17, 'messages: индекс под запросы «от получателя»'),
  (19, 'chat_pins: закреплённые ЧАТЫ (общие для ПК и телефона)'),
  (20, 'file_sha: отпечаток вложения, чтобы не заливать одно и то же дважды');


-- ── Проверка ────────────────────────────────────────────────────────────────
-- В журнале должны появиться отмеченные номера, а file_sha — в трёх таблицах.
SELECT id, name, applied_at FROM schema_migrations ORDER BY id;

SELECT TABLE_NAME, COLUMN_NAME
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME = 'file_sha'
ORDER BY TABLE_NAME;
