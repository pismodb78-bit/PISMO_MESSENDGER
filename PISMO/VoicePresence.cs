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

        /// <summary>Отмечает/обновляет присутствие пользователя в канале (heartbeat).
        /// streaming=true, если у пользователя включена камера или демонстрация экрана.</summary>
        public static void Heartbeat(int channelId, int userId, bool streaming = false,
                                     bool micMuted = false, bool deafened = false)
        {
            if (!_tableOk || channelId <= 0 || userId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO voice_presence (channel_id,user_id,joined_at,last_seen,streaming,mic_muted,deafened) " +
                    "VALUES (@c,@u,NOW(),NOW(),@st,@mm,@df) " +
                    "ON DUPLICATE KEY UPDATE last_seen=NOW(), streaming=@st, mic_muted=@mm, deafened=@df", conn);
                cmd.Parameters.AddWithValue("@c", channelId);
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@st", streaming ? 1 : 0);
                cmd.Parameters.AddWithValue("@mm", micMuted ? 1 : 0);
                cmd.Parameters.AddWithValue("@df", deafened ? 1 : 0);
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
        public static Dictionary<int, List<(int uid, string name, bool streaming, bool micMuted, bool deafened)>> ReadForServer(int serverId)
        {
            var map = new Dictionary<int, List<(int, string, bool, bool, bool)>>();
            if (!_tableOk || serverId <= 0) return map;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT vp.channel_id, vp.user_id, vp.streaming, vp.mic_muted, vp.deafened, " +
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
                    bool streaming = r["streaming"] != DBNull.Value && Convert.ToInt32(r["streaming"]) != 0;
                    bool micMuted = r["mic_muted"] != DBNull.Value && Convert.ToInt32(r["mic_muted"]) != 0;
                    bool deafened = r["deafened"] != DBNull.Value && Convert.ToInt32(r["deafened"]) != 0;
                    string nm = r["nm"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"]?.ToString();
                    if (!map.TryGetValue(cid, out var list)) { list = new(); map[cid] = list; }
                    list.Add((uid, nm ?? "", streaming, micMuted, deafened));
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
