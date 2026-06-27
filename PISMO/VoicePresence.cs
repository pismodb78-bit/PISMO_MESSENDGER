using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Присутствие в голосовых каналах серверов («кто в эфире сейчас», как в
    /// Discord). Клиент, пока открыта форма голосового канала, периодически
    /// шлёт heartbeat; ServersForm читает список участников по channel_id.
    ///
    /// channel_id извлекается из имени комнаты вида "vch_&lt;id&gt;".
    /// Запись считается живой, если last_seen обновлялся в последние ~20 сек.
    /// </summary>
    public static class VoicePresence
    {
        private static bool _tableOk = true;

        /// <summary>"vch_123" -> 123, иначе -1.</summary>
        public static int ChannelIdFromRoom(string room)
        {
            if (string.IsNullOrEmpty(room)) return -1;
            if (room.StartsWith("vch_", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(room.Substring(4), out int id)) return id;
            return -1;
        }

        /// <summary>Отмечает/обновляет присутствие пользователя в канале (heartbeat).</summary>
        public static void Heartbeat(int channelId, int userId)
        {
            if (!_tableOk || channelId <= 0 || userId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO voice_presence (channel_id,user_id,joined_at,last_seen) " +
                    "VALUES (@c,@u,NOW(),NOW()) " +
                    "ON DUPLICATE KEY UPDATE last_seen=NOW()", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }
            catch (MySqlException mex)
            {
                if (mex.Number == 1146) _tableOk = false; // таблицы нет — миграция не выполнена
            }
            catch { }
        }

        /// <summary>Убирает пользователя из канала (при выходе из звонка).</summary>
        public static void Leave(int channelId, int userId)
        {
            if (!_tableOk || channelId <= 0 || userId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM voice_presence WHERE channel_id=@c AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Список «живых» участников всех голосовых каналов сервера:
        /// channelId -> [(userId, name)]. Протухшие записи (нет heartbeat &gt; 20 c) игнорируются.</summary>
        public static Dictionary<int, List<(int uid, string name)>> ReadForServer(int serverId)
        {
            var map = new Dictionary<int, List<(int, string)>>();
            if (!_tableOk || serverId <= 0) return map;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT vp.channel_id, vp.user_id, " +
                    "TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login " +
                    "FROM voice_presence vp " +
                    "JOIN server_channels sc ON sc.id = vp.channel_id " +
                    "JOIN users u ON u.id = vp.user_id " +
                    "WHERE sc.server_id=@s AND vp.last_seen > (NOW() - INTERVAL 20 SECOND) " +
                    "ORDER BY vp.joined_at ASC", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int cid = Convert.ToInt32(r["channel_id"]);
                    int uid = Convert.ToInt32(r["user_id"]);
                    string nm = r["nm"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"]?.ToString();
                    if (!map.TryGetValue(cid, out var list)) { list = new(); map[cid] = list; }
                    list.Add((uid, nm ?? ""));
                }
            }
            catch (MySqlException mex)
            {
                if (mex.Number == 1146) _tableOk = false;
            }
            catch { }
            return map;
        }
    }
}
