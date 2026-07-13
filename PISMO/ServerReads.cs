using System;
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
