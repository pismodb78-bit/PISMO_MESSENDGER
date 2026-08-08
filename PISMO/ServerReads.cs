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
        public static List<Badge> GetBadges(int userId, string myLogin)
        {
            var list = new List<Badge>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT sc.server_id, sm.channel_id, mm.muted_notifs, COUNT(*) AS unread, " +
                    "SUM(CASE WHEN LOWER(sm.text) LIKE CONCAT('%@', @login, '%') " +
                    "  OR (rr.name IS NOT NULL AND rr.name <> '' AND LOWER(sm.text) LIKE CONCAT('%@', LOWER(rr.name), '%')) " +
                    "  OR LOWER(sm.text) LIKE '%@все%' OR LOWER(sm.text) LIKE '%@all%' OR LOWER(sm.text) LIKE '%@everyone%' " +
                    "  OR EXISTS(SELECT 1 FROM server_messages p WHERE p.id = sm.reply_to_id AND p.sender_id = @me) " +
                    "  THEN 1 ELSE 0 END) AS mentions " +
                    "FROM server_messages sm " +
                    "JOIN server_channels sc ON sc.id = sm.channel_id " +
                    "JOIN server_members mm ON mm.server_id = sc.server_id AND mm.user_id = @me " +
                    "LEFT JOIN server_roles rr ON rr.id = mm.role_id " +
                    "LEFT JOIN server_reads r ON r.user_id = @me AND r.channel_id = sm.channel_id " +
                    "WHERE sm.sender_id <> @me AND sm.is_deleted = 0 " +
                    "  AND sm.id > COALESCE(r.last_read_id, 0) " +
                    "GROUP BY sc.server_id, sm.channel_id, mm.muted_notifs", conn);
                cmd.Parameters.AddWithValue("@me", userId);
                cmd.Parameters.AddWithValue("@login", (myLogin ?? "").ToLowerInvariant());
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
