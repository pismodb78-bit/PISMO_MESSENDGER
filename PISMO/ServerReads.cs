using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Метки «прочитано» для каналов серверов (таблица server_reads, миграция 8).
    /// «Пометить прочитанным» канал / весь сервер — без захода в чаты.
    /// </summary>
    public static class ServerReads
    {
        /// <summary>Пометить канал прочитанным (last_read_id = максимум канала).</summary>
        public static void MarkChannelRead(int userId, int channelId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO server_reads (user_id, channel_id, last_read_id) " +
                    "SELECT @u, @c, COALESCE(MAX(id),0) FROM server_messages WHERE channel_id=@c " +
                    "ON DUPLICATE KEY UPDATE last_read_id=VALUES(last_read_id)", conn);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public struct Badge { public int ServerId; public int ChannelId; public int Unread; public int Mentions; public bool Muted; }

        /// <summary>Непрочитанные и упоминания по КАЖДОМУ каналу всех серверов
        /// пользователя (одним запросом). Упоминание = @login / @роль-на-ЭТОМ-сервере /
        /// @все|@all|@everyone в тексте, ИЛИ ответ на моё сообщение. Роль берётся из
        /// server_members.role_id → server_roles.name (индивидуально по серверу).
        /// Muted = заглушён ли сервер ДЛЯ ЭТОГО пользователя (server_members.muted_notifs,
        /// строго по user_id — не влияет на других). Учитываются чужие неудалённые
        /// сообщения новее last_read_id.</summary>
        // Кэш наличия необязательных колонок server_messages (миграции применены
        // не на всех БД: reply_to_id может отсутствовать, is_deleted в текущей схеме
        // нет вовсе). Раньше запрос жёстко ссылался на обе колонки — при их
        // отсутствии он падал, catch{} возвращал пустой список, и ВСЕ бейджи
        // (и упоминания, и непрочитанные) пропадали.
        private static bool? _hasReplyCol;
        private static bool? _hasDeletedCol;
        private static bool? _hasMentionsTbl;

        // На части хостингов доступ к information_schema закрыт даже администратору
        // (#1044). Тогда эти проверки возвращали false, и вместе с ними молча
        // отваливались и «ответ на моё сообщение», и фильтр удалённых. Фолбэк —
        // SHOW-запросы: это встроенные команды сервера, им хватает обычных прав.
        private static bool ColumnExists(MySqlConnection conn, string table, string col)
        {
            try
            {
                using var c = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.columns " +
                    "WHERE table_schema = DATABASE() AND table_name=@t AND column_name=@c", conn);
                c.Parameters.AddWithValue("@t", table);
                c.Parameters.AddWithValue("@c", col);
                return Convert.ToInt32(c.ExecuteScalar()) > 0;
            }
            catch (MySqlException)
            {
                try
                {
                    if (!IsPlainIdentifier(table)) return false;
                    using var c = new MySqlCommand($"SHOW COLUMNS FROM `{table}`", conn);
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        if (string.Equals(r.GetString(0), col, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                return false;
            }
            catch { return false; }
        }

        private static bool TableExists(MySqlConnection conn, string table)
        {
            try
            {
                using var c = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() AND table_name=@t", conn);
                c.Parameters.AddWithValue("@t", table);
                return Convert.ToInt32(c.ExecuteScalar()) > 0;
            }
            catch (MySqlException)
            {
                try
                {
                    using var c = new MySqlCommand("SHOW TABLES", conn);
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        if (string.Equals(r.GetString(0), table, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
                return false;
            }
            catch { return false; }
        }

        private static bool IsPlainIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        public static List<Badge> GetBadges(int userId, string myLogin)
        {
            var list = new List<Badge>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                _hasReplyCol ??= ColumnExists(conn, "server_messages", "reply_to_id");
                _hasDeletedCol ??= ColumnExists(conn, "server_messages", "is_deleted");

                // Слагаемые «что считать упоминанием» собираем списком: части
                // необязательные (колонки/таблицы может не быть), а склейка строк с
                // ручной обрезкой «OR» слишком легко даёт битый SQL.
                var mentionParts = new List<string>();
                if (_hasReplyCol == true)
                    mentionParts.Add("EXISTS(SELECT 1 FROM server_messages p WHERE p.id = sm.reply_to_id AND p.sender_id = @me)");
                // Фильтр удалённых — только если колонка есть.
                string notDeleted = _hasDeletedCol == true ? "AND sm.is_deleted = 0 " : "";

                // Упоминания берём из server_mentions (миграция 15). Прежний вариант
                // искал «@логин» через LIKE по sm.text — а текст в БД ЗАШИФРОВАН
                // (Crypto.Enc, AES-GCM в Base64), где символа «@» нет вовсе, так что
                // условие не выполнялось НИКОГДА и красная цифра не работала.
                _hasMentionsTbl ??= TableExists(conn, "server_mentions");
                if (_hasMentionsTbl == true)
                    mentionParts.Add("EXISTS(SELECT 1 FROM server_mentions mn WHERE mn.message_id = sm.id AND mn.user_id = @me)");

                // Нет ни таблицы, ни колонки ответов — считать нечего, но SQL обязан
                // остаться валидным: подставляем заведомо ложное условие.
                string mentionExpr = mentionParts.Count > 0
                    ? string.Join(" OR ", mentionParts) + " "
                    : "0=1 ";

                using var cmd = new MySqlCommand(
                    "SELECT sc.server_id, sm.channel_id, mm.muted_notifs, COUNT(*) AS unread, " +
                    "SUM(CASE WHEN " + mentionExpr + "THEN 1 ELSE 0 END) AS mentions " +
                    "FROM server_messages sm " +
                    "JOIN server_channels sc ON sc.id = sm.channel_id " +
                    "JOIN server_members mm ON mm.server_id = sc.server_id AND mm.user_id = @me " +
                    "LEFT JOIN server_reads r ON r.user_id = @me AND r.channel_id = sm.channel_id " +
                    "WHERE sm.sender_id <> @me " + notDeleted +
                    "  AND sm.id > COALESCE(r.last_read_id, 0) " +
                    "GROUP BY sc.server_id, sm.channel_id, mm.muted_notifs", conn);
                cmd.Parameters.AddWithValue("@me", userId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Badge
                    {
                        ServerId = Convert.ToInt32(r["server_id"]),
                        ChannelId = Convert.ToInt32(r["channel_id"]),
                        Unread = Convert.ToInt32(r["unread"]),
                        Mentions = r["mentions"] == DBNull.Value ? 0 : Convert.ToInt32(r["mentions"]),
                        Muted = r["muted_notifs"] != DBNull.Value && Convert.ToInt32(r["muted_notifs"]) == 1
                    });
            }
            catch { }
            return list;
        }

        /// <summary>Пометить прочитанными ВСЕ каналы сервера (текстовые и голосовые).</summary>
        public static void MarkServerRead(int userId, int serverId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO server_reads (user_id, channel_id, last_read_id) " +
                    "SELECT @u, ch.id, COALESCE((SELECT MAX(sm.id) FROM server_messages sm WHERE sm.channel_id=ch.id),0) " +
                    "FROM server_channels ch WHERE ch.server_id=@s " +
                    "ON DUPLICATE KEY UPDATE last_read_id=VALUES(last_read_id)", conn);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
