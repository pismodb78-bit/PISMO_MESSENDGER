using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// PISMO 2.0 — версионированные миграции схемы БД (единый источник правды).
    ///
    /// Раньше изменения схемы были разбросаны по коду (EnsureTable/EnsureColumn,
    /// ручные ALTER), из-за чего появлялись баги вроде «Unknown column f.status»
    /// на импортированных базах. Теперь ВСЕ изменения — здесь, по порядку, с
    /// номером версии. Применённые миграции фиксируются в таблице
    /// schema_migrations, поэтому каждая выполняется РОВНО один раз, а на любой
    /// существующей базе достаётся ровно то, чего не хватает.
    ///
    /// Как добавить изменение схемы в будущем: допиши новый пункт в Migrations с
    /// БОЛЬШИМ Id и идемпотентным телом (CREATE TABLE IF NOT EXISTS / проверка
    /// колонки перед ALTER через ColumnExists). НИЧЕГО не меняй в уже
    /// применённых миграциях — только добавляй новые.
    /// </summary>
    public static class DbMigrator
    {
        private static bool _done;
        private static readonly object _lock = new();

        /// <summary>Список миграций (Id должен строго возрастать).</summary>
        private static readonly List<(int Id, string Name, Action<MySqlConnection> Up)> Migrations = new()
        {
            (1, "friends: заявки + status", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS friends (" +
                    "user_id INT NOT NULL, friend_id INT NOT NULL, " +
                    "status TINYINT NOT NULL DEFAULT 0, " +
                    "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (user_id, friend_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
                // Старая таблица friends без status → добавляем (DEFAULT 1 = уже друзья).
                if (!ColumnExists(conn, "friends", "status"))
                    Exec(conn, "ALTER TABLE friends ADD COLUMN status TINYINT NOT NULL DEFAULT 1");
            }),

            (2, "user_prefs: приватность ЛС", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS user_prefs (" +
                    "user_id INT NOT NULL PRIMARY KEY, " +
                    "dm_privacy TINYINT NOT NULL DEFAULT 0) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            (3, "users.dm_privacy (запасное хранилище)", conn =>
            {
                if (TableExists(conn, "users") && !ColumnExists(conn, "users", "dm_privacy"))
                    Exec(conn, "ALTER TABLE users ADD COLUMN dm_privacy TINYINT NOT NULL DEFAULT 0");
            }),

            (4, "message_reactions: реакции на сообщения", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS message_reactions (" +
                    "message_id INT NOT NULL, " +
                    "scope TINYINT NOT NULL DEFAULT 0, " +   // 0=личное, 1=групповое, 2=серверное
                    "user_id INT NOT NULL, " +
                    "emoji VARCHAR(16) NOT NULL, " +
                    "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (message_id, scope, user_id, emoji), " +
                    "KEY idx_react_msg (message_id, scope)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            (5, "pinned_messages: закреплённые сообщения", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS pinned_messages (" +
                    "message_id INT NOT NULL, " +
                    "scope TINYINT NOT NULL DEFAULT 0, " +   // 0=личное, 1=групповое
                    "pinned_by INT NOT NULL, " +
                    "pinned_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (message_id, scope)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            (6, "message_edits: история изменений сообщений", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS message_edits (" +
                    "id INT NOT NULL AUTO_INCREMENT, " +
                    "message_id INT NOT NULL, " +
                    "scope TINYINT NOT NULL DEFAULT 0, " +   // 0=личное, 1=групповое
                    "old_text TEXT NULL, " +                 // прежний текст (зашифрован)
                    "edited_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (id), KEY idx_edits_msg (message_id, scope)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            (7, "message_reactions: PK с эмодзи (несколько реакций от одного юзера)", conn =>
            {
                // На «живой» БД таблица могла быть создана раньше — со СТАРЫМ
                // первичным ключом (message_id, scope, user_id) без emoji. Тогда
                // один пользователь мог поставить лишь ОДНУ реакцию на сообщение:
                // вторая реакция другим эмодзи молча отсекалась INSERT IGNORE.
                // Чиним — добавляем emoji в PK, если его там ещё нет.
                bool emojiInPk = false;
                try
                {
                    using var q = new MySqlCommand(
                        "SELECT COUNT(*) FROM information_schema.STATISTICS " +
                        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'message_reactions' " +
                        "AND INDEX_NAME = 'PRIMARY' AND COLUMN_NAME = 'emoji'", conn);
                    emojiInPk = Convert.ToInt32(q.ExecuteScalar()) > 0;
                }
                catch { }
                if (!emojiInPk)
                    Exec(conn, "ALTER TABLE message_reactions DROP PRIMARY KEY, " +
                               "ADD PRIMARY KEY (message_id, scope, user_id, emoji)");
            }),

            (8, "server_reads: метки прочитанного в каналах серверов", conn =>
            {
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS server_reads (" +
                    "user_id INT NOT NULL, " +
                    "channel_id INT NOT NULL, " +
                    "last_read_id INT NOT NULL DEFAULT 0, " +
                    "read_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (user_id, channel_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            (9, "message_reactions.emoji: бинарная коллация (разные эмодзи ≠ равны)", conn =>
            {
                // КОВАРНЫЙ баг MySQL: в коллациях utf8mb4_general_ci/unicode_ci
                // РАЗНЫЕ эмодзи сравниваются как РАВНЫЕ строки. Из-за этого:
                //  • WHERE emoji=@e находил «ту же» реакцию для любого эмодзи —
                //    тумблер УДАЛЯЛ первую реакцию при попытке поставить вторую;
                //  • PK с emoji тоже считал две разные реакции дубликатом.
                // utf8mb4_bin сравнивает по кодпоинтам — каждый эмодзи уникален.
                Exec(conn, "ALTER TABLE message_reactions " +
                           "MODIFY emoji VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL");
            }),

            (10, "message_reactions: пересборка PK с emoji ПОСЛЕ бинарной коллации", conn =>
            {
                // Недостающее звено: миграция 7 добавляла emoji в PK ещё под _ci —
                // и на «живой» БД падала (разные эмодзи = «дубликаты»), оставляя PK
                // (message_id, scope, user_id) → один юзер = ОДНА реакция. Миграция 9
                // сделала колонку бинарной, но PK не трогала. Здесь — гарантированно
                // пересобираем PK с emoji (теперь эмодзи байт-различны, коллизий нет).
                try
                {
                    Exec(conn, "ALTER TABLE message_reactions " +
                               "MODIFY emoji VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL");
                }
                catch { }

                bool emojiInPk = false;
                try
                {
                    using var q = new MySqlCommand(
                        "SELECT COUNT(*) FROM information_schema.STATISTICS " +
                        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'message_reactions' " +
                        "AND INDEX_NAME = 'PRIMARY' AND COLUMN_NAME = 'emoji'", conn);
                    emojiInPk = Convert.ToInt32(q.ExecuteScalar()) > 0;
                }
                catch { }

                if (!emojiInPk)
                {
                    bool hasPk = false;
                    try
                    {
                        using var q2 = new MySqlCommand(
                            "SELECT COUNT(*) FROM information_schema.STATISTICS " +
                            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'message_reactions' " +
                            "AND INDEX_NAME = 'PRIMARY'", conn);
                        hasPk = Convert.ToInt32(q2.ExecuteScalar()) > 0;
                    }
                    catch { }

                    if (hasPk) { try { Exec(conn, "ALTER TABLE message_reactions DROP PRIMARY KEY"); } catch { } }
                    Exec(conn, "ALTER TABLE message_reactions " +
                               "ADD PRIMARY KEY (message_id, scope, user_id, emoji)");
                }
            }),

            (11, "voice_presence: значки мьюта микрофона/наушников (как в Discord)", conn =>
            {
                if (TableExists(conn, "voice_presence"))
                {
                    if (!ColumnExists(conn, "voice_presence", "mic_muted"))
                        Exec(conn, "ALTER TABLE voice_presence ADD COLUMN mic_muted TINYINT NOT NULL DEFAULT 0");
                    if (!ColumnExists(conn, "voice_presence", "deafened"))
                        Exec(conn, "ALTER TABLE voice_presence ADD COLUMN deafened TINYINT NOT NULL DEFAULT 0");
                }
            }),

            (12, "text-колонки сообщений → LONGTEXT (баг «Data too long for column 'text'»)", conn =>
            {
                // На части инсталляций колонка text была создана как VARCHAR(...)
                // (легаси-схема), из-за чего длинные сообщения и длинные подписи к
                // файлам падали с «Data too long for column 'text' at row 1».
                // Приводим к LONGTEXT (4 ГБ) во всех таблицах сообщений. MODIFY
                // не трогает данные и идемпотентен, поэтому гоняем безусловно, но
                // только для существующих таблиц с колонкой text.
                // strict-режим MySQL превращает предупреждение о конвертации
                // кодировки в ошибку (#1265) и обрывает ALTER — снимаем его на
                // текущую сессию. Данные не теряются: шифр это Base64 (ASCII),
                // старый текст — валидный UTF-8.
                try { Exec(conn, "SET SESSION sql_mode = ''"); } catch { }
                foreach (var t in new[] { "messages", "group_messages", "server_messages" })
                {
                    if (TableExists(conn, t) && ColumnExists(conn, t, "text"))
                    {
                        try { Exec(conn, $"ALTER TABLE {t} MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NOT NULL"); }
                        catch { Exec(conn, $"ALTER TABLE {t} MODIFY text LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci NULL"); }
                    }
                }
            }),

            (13, "server_messages.reply_to_id: ответы в каналах серверов", conn =>
            {
                // На части инсталляций колонки нет (в т.ч. в текущей БД) — из-за
                // этого ответы в каналах не хранились, а красная цифра «ответ на
                // моё сообщение» на сервере не могла считаться. Добавляем, если
                // таблица есть и колонки ещё нет; индекс — для быстрого поиска ответов.
                if (TableExists(conn, "server_messages") && !ColumnExists(conn, "server_messages", "reply_to_id"))
                {
                    Exec(conn, "ALTER TABLE server_messages ADD COLUMN reply_to_id INT UNSIGNED NULL");
                    try { Exec(conn, "ALTER TABLE server_messages ADD KEY idx_reply (reply_to_id)"); } catch { }
                }
            }),

            (14, "server_channels.user_limit: вместимость голосового канала", conn =>
            {
                // 0 = без ограничения (поведение до этой миграции сохраняется на
                // всех существующих каналах).
                if (TableExists(conn, "server_channels") && !ColumnExists(conn, "server_channels", "user_limit"))
                    Exec(conn, "ALTER TABLE server_channels ADD COLUMN user_limit INT NOT NULL DEFAULT 0");
            }),

            (15, "server_mentions: упоминания в каналах (текст в БД зашифрован)", conn =>
            {
                // Считать упоминания запросом LIKE по server_messages.text нельзя:
                // текст хранится зашифрованным (AES-GCM в Base64), и «@логин» там не
                // встретится никогда. Адресаты вычисляются при отправке и лежат тут.
                Exec(conn,
                    "CREATE TABLE IF NOT EXISTS server_mentions (" +
                    "message_id BIGINT NOT NULL, " +
                    "channel_id INT NOT NULL, " +
                    "user_id INT NOT NULL, " +
                    "PRIMARY KEY (message_id, user_id), " +
                    "KEY idx_mention_lookup (user_id, channel_id, message_id)" +
                    ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
            }),

            // ВНИМАНИЕ ПРО НОМЕРА. Журнал schema_migrations общий с Android-клиентом:
            // это одна и та же база. Значит, номер 16 обязан означать ОДНО И ТО ЖЕ на
            // обеих сторонах — иначе тот, кто запустится вторым, увидит номер в журнале
            // и молча пропустит СВОЮ миграцию. 16, 17 и 18 ниже повторяют одноимённые
            // миграции Android слово в слово.

            (16, "server_roles.can_channels: отдельное право на каналы", conn =>
            {
                if (TableExists(conn, "server_roles") && !ColumnExists(conn, "server_roles", "can_channels"))
                {
                    Exec(conn, "ALTER TABLE server_roles ADD COLUMN can_channels TINYINT(1) NOT NULL DEFAULT 0");
                    // У кого было общее управление — тот не должен потерять каналы.
                    Exec(conn, "UPDATE server_roles SET can_channels=1 WHERE can_manage=1");
                }
            }),

            (17, "messages: индексы под горячие запросы личных сообщений", conn =>
            {
                // ЭТО ЛЕЧИТ «ДИСК НА 100 МБ ПРИ КАЖДОМ СООБЩЕНИИ».
                //
                // В messages есть только idx_msg_pair (sender_id, receiver_id) и
                // idx_msg_created (created_at). Запросы, которые идут ОТ ПОЛУЧАТЕЛЯ,
                // опереться на них не могут вовсе:
                //
                //   ReadUnreadCounts  — WHERE receiver_id=@me AND is_read=0
                //   LoadConversations — ветка «мне писали»
                //   MarkAsRead        — WHERE ... AND is_read=0
                //
                // Без индекса каждый из них сканирует messages ЦЕЛИКОМ. Первый из
                // них уходит на каждый тик опроса и дополнительно на каждое событие
                // «новое сообщение» — отсюда и полка чтения на десятки секунд после
                // каждой отправки, хоть текст и весит сотню байт.
                //
                // Те же индексы лежат в sql/2026-08-19_message_indexes.sql для ручного
                // применения — на случай, если у учётной записи нет права ALTER.
                AddIndex(conn, "messages", "idx_msg_recv_read", "(receiver_id, is_read, sender_id)");
                AddIndex(conn, "messages", "idx_msg_recv_time", "(receiver_id, created_at, id)");
                AddIndex(conn, "messages", "idx_msg_send_time", "(sender_id, created_at, id)");
                AddIndex(conn, "messages", "idx_msg_pair_time", "(sender_id, receiver_id, id)");
            }),

            (18, "индексы лент: messages, group_messages, server_messages", conn =>
            {
                // Индексы messages перечислены здесь ЕЩЁ РАЗ намеренно. Их же
                // добавляет миграция 17 — но в Android-клиенте 17 проверяла наличие
                // индекса через information_schema и при отказе в доступе (#1044)
                // считала, что индекс уже есть: могла ничего не создать и при этом
                // отметиться в общем журнале. Тогда 17 здесь просто пропустится.
                // Эта миграция ничего не спрашивает и просто пробует создать.
                AddIndex(conn, "messages", "idx_msg_recv_read", "(receiver_id, is_read, sender_id)");
                AddIndex(conn, "messages", "idx_msg_recv_time", "(receiver_id, created_at, id)");
                AddIndex(conn, "messages", "idx_msg_send_time", "(sender_id, created_at, id)");
                AddIndex(conn, "messages", "idx_msg_pair_time", "(sender_id, receiver_id, id)");

                // То же самое для групп и каналов: опрос спрашивает «есть ли новое»
                // максимальным id, лента берёт последнюю страницу. И то и другое
                // должно быть движением к концу индекса, а не проходом по всей
                // истории — строки там широкие, с вложениями.
                if (TableExists(conn, "group_messages"))
                    AddIndex(conn, "group_messages", "idx_gm_group_id", "(group_id, id)");
                if (TableExists(conn, "server_messages"))
                    AddIndex(conn, "server_messages", "idx_sm_channel_id", "(channel_id, id)");
            }),
        };

        /// <summary>Максимальный номер применённой миграции после Run (для инфо).</summary>
        public static int CurrentVersion { get; private set; }

        /// <summary>Прогоняет все ещё не применённые миграции по порядку. Безопасно
        /// вызывать многократно и параллельно; при отсутствии связи — тихо выходит
        /// (миграции применятся при следующем успешном запуске).</summary>
        public static void Run()
        {
            lock (_lock)
            {
                if (_done) return;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    EnsureLedger(conn);
                    var applied = LoadApplied(conn);

                    foreach (var m in Migrations)
                    {
                        if (applied.Contains(m.Id)) continue;
                        try
                        {
                            m.Up(conn);
                            MarkApplied(conn, m.Id, m.Name);
                            CurrentVersion = Math.Max(CurrentVersion, m.Id);
                            System.Diagnostics.Debug.WriteLine($"[DbMigrator] ✓ {m.Id} {m.Name}");
                        }
                        catch (Exception ex)
                        {
                            // Одна миграция упала — не блокируем остальные и не
                            // помечаем её применённой (повторим в следующий раз).
                            System.Diagnostics.Debug.WriteLine($"[DbMigrator] ✗ {m.Id} {m.Name}: {ex.Message}");
                        }
                    }
                    _done = true;   // за эту сессию больше не гоняем
                }
                catch (Exception ex)
                {
                    // Нет связи с БД — попробуем в следующий раз (флаг не ставим).
                    System.Diagnostics.Debug.WriteLine($"[DbMigrator] нет связи: {ex.Message}");
                }
            }
        }

        // ── Журнал применённых миграций ─────────────────────────────────
        private static void EnsureLedger(MySqlConnection conn)
        {
            Exec(conn,
                "CREATE TABLE IF NOT EXISTS schema_migrations (" +
                "id INT NOT NULL PRIMARY KEY, " +
                "name VARCHAR(255) NULL, " +
                "applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        }

        private static HashSet<int> LoadApplied(MySqlConnection conn)
        {
            var set = new HashSet<int>();
            try
            {
                using var cmd = new MySqlCommand("SELECT id FROM schema_migrations", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(Convert.ToInt32(r["id"]));
            }
            catch { }
            return set;
        }

        private static void MarkApplied(MySqlConnection conn, int id, string name)
        {
            using var cmd = new MySqlCommand(
                "INSERT IGNORE INTO schema_migrations (id, name) VALUES (@id, @name)", conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name ?? "");
            cmd.ExecuteNonQuery();
        }

        // ── Помощники (доступны миграциям) ──────────────────────────────
        private static void Exec(MySqlConnection conn, string sql)
        {
            using var cmd = new MySqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Создаёт индекс, если его ещё нет.
        ///
        /// Существование НЕ проверяем заранее: на части хостингов закрыт доступ и к
        /// information_schema, и это уронило бы миграцию до самого ALTER. Вместо
        /// проверки просто пробуем создать и глотаем ошибку 1061 «Duplicate key
        /// name» — она означает ровно то, что нам и нужно: индекс уже на месте.
        /// Любая другая ошибка (например, нет права ALTER) уходит наверх, и
        /// миграция не отмечается применённой — повторится при следующем запуске,
        /// а до тех пор индексы можно положить руками скриптом из папки sql.
        /// </summary>
        private static void AddIndex(MySqlConnection conn, string table, string name, string columns)
        {
            if (!IsPlainIdentifier(table) || !IsPlainIdentifier(name)) return;
            try
            {
                Exec(conn, $"ALTER TABLE `{table}` ADD INDEX `{name}` {columns}");
            }
            catch (MySqlException e) when (e.Number == 1061)
            {
                // уже есть — это успех
            }
        }

        // На части хостингов доступ к information_schema закрыт даже
        // администратору («#1044 — доступ к базе данных 'information_schema'
        // закрыт»). Тогда проверки существования таблицы/колонки падали ещё
        // ДО самого ALTER, и миграция не применялась и не отмечалась —
        // повторялась при каждом запуске. Фолбэк — SHOW-запросы: это
        // встроенные команды сервера, им хватает обычных прав на таблицу.

        private static bool TableExists(MySqlConnection conn, string table)
        {
            try
            {
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.TABLES " +
                    "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t", conn);
                cmd.Parameters.AddWithValue("@t", table);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch (MySqlException)
            {
                using var cmd = new MySqlCommand("SHOW TABLES", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(0), table, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        private static bool ColumnExists(MySqlConnection conn, string table, string column)
        {
            try
            {
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                    "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND COLUMN_NAME=@c", conn);
                cmd.Parameters.AddWithValue("@t", table);
                cmd.Parameters.AddWithValue("@c", column);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch (MySqlException)
            {
                // Имя таблицы приходит только из констант в списке миграций,
                // но подстановку в SHOW всё равно ограничиваем идентификатором.
                if (!IsPlainIdentifier(table)) throw;
                using var cmd = new MySqlCommand($"SHOW COLUMNS FROM `{table}`", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (string.Equals(r.GetString(0), column, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        private static bool IsPlainIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }
    }
}
