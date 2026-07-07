using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// PISMO 2.0 — реакции-эмодзи на сообщения (как в Discord). Хранятся в
    /// message_reactions (см. DbMigrator migration 4). scope: 0=личное сообщение,
    /// 1=групповое, 2=серверное — чтобы id из разных таблиц не путались.
    /// </summary>
    public static class ReactionsRepository
    {
        public enum Scope { Direct = 0, Group = 1, Server = 2 }

        /// <summary>Одна реакция на сообщении: эмодзи, сколько людей, поставил ли я.</summary>
        public sealed class Reaction
        {
            public string Emoji;
            public int Count;
            public bool Mine;
        }

        /// <summary>Поставить/снять реакцию (тумблер). Возвращает true, если после
        /// операции реакция стоит (поставлена), false — снята.</summary>
        public static bool Toggle(int messageId, Scope scope, int userId, string emoji)
        {
            if (messageId <= 0 || string.IsNullOrWhiteSpace(emoji)) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Уже стоит?
                using (var chk = new MySqlCommand(
                    "SELECT 1 FROM message_reactions WHERE message_id=@m AND scope=@s AND user_id=@u AND emoji=@e", conn))
                {
                    chk.Parameters.AddWithValue("@m", messageId);
                    chk.Parameters.AddWithValue("@s", (int)scope);
                    chk.Parameters.AddWithValue("@u", userId);
                    chk.Parameters.AddWithValue("@e", emoji);
                    if (chk.ExecuteScalar() != null)
                    {
                        using var del = new MySqlCommand(
                            "DELETE FROM message_reactions WHERE message_id=@m AND scope=@s AND user_id=@u AND emoji=@e", conn);
                        del.Parameters.AddWithValue("@m", messageId);
                        del.Parameters.AddWithValue("@s", (int)scope);
                        del.Parameters.AddWithValue("@u", userId);
                        del.Parameters.AddWithValue("@e", emoji);
                        del.ExecuteNonQuery();
                        return false;
                    }
                }
                using (var ins = new MySqlCommand(
                    "INSERT IGNORE INTO message_reactions (message_id, scope, user_id, emoji) VALUES (@m, @s, @u, @e)", conn))
                {
                    ins.Parameters.AddWithValue("@m", messageId);
                    ins.Parameters.AddWithValue("@s", (int)scope);
                    ins.Parameters.AddWithValue("@u", userId);
                    ins.Parameters.AddWithValue("@e", emoji);
                    ins.ExecuteNonQuery();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Реакции СРАЗУ для набора сообщений — один запрос на отрисовку
        /// (без роундтрипа на каждое сообщение). Ключ — message_id.</summary>
        public static Dictionary<int, List<Reaction>> ForMessages(IEnumerable<int> ids, Scope scope, int myId)
        {
            var map = new Dictionary<int, List<Reaction>>();
            var list = new List<int>(ids ?? Array.Empty<int>());
            if (list.Count == 0) return map;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT message_id, emoji, COUNT(*) AS cnt, " +
                    "MAX(CASE WHEN user_id=@me THEN 1 ELSE 0 END) AS mine " +
                    "FROM message_reactions WHERE scope=@s AND message_id IN (" + string.Join(",", list) + ") " +
                    "GROUP BY message_id, emoji ORDER BY MIN(created_at)", conn);
                cmd.Parameters.AddWithValue("@s", (int)scope);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int mid = Convert.ToInt32(r["message_id"]);
                    if (!map.TryGetValue(mid, out var l)) { l = new List<Reaction>(); map[mid] = l; }
                    l.Add(new Reaction
                    {
                        Emoji = r["emoji"].ToString(),
                        Count = Convert.ToInt32(r["cnt"]),
                        Mine = Convert.ToInt32(r["mine"]) == 1
                    });
                }
            }
            catch { }
            return map;
        }

        /// <summary>Реакции одного сообщения (сгруппированы по эмодзи, с флагом «моя»).</summary>
        public static List<Reaction> ForMessage(int messageId, Scope scope, int myId)
        {
            var list = new List<Reaction>();
            if (messageId <= 0) return list;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT emoji, COUNT(*) AS cnt, " +
                    "MAX(CASE WHEN user_id=@me THEN 1 ELSE 0 END) AS mine " +
                    "FROM message_reactions WHERE message_id=@m AND scope=@s " +
                    "GROUP BY emoji ORDER BY MIN(created_at)", conn);
                cmd.Parameters.AddWithValue("@m", messageId);
                cmd.Parameters.AddWithValue("@s", (int)scope);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new Reaction
                    {
                        Emoji = r["emoji"].ToString(),
                        Count = Convert.ToInt32(r["cnt"]),
                        Mine = Convert.ToInt32(r["mine"]) == 1
                    });
            }
            catch { }
            return list;
        }
    }
}
