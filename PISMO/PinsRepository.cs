using System;
using System.Collections.Generic;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// PISMO 2.0 — закреплённые сообщения (как в Discord). Хранятся в
    /// pinned_messages (см. DbMigrator migration 5). scope: 0=личное, 1=групповое.
    /// </summary>
    public static class PinsRepository
    {
        public sealed class PinnedItem
        {
            public int MessageId;
            public string Sender;
            public string TextCipher;   // текст в БД (зашифрован) — расшифровывать через Crypto.Dec
        }

        public static bool IsPinned(int messageId, int scope)
        {
            if (messageId <= 0) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM pinned_messages WHERE message_id=@m AND scope=@s", conn);
                cmd.Parameters.AddWithValue("@m", messageId);
                cmd.Parameters.AddWithValue("@s", scope);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        /// <summary>Закрепить/открепить (тумблер). Возвращает итоговое состояние.</summary>
        public static bool Toggle(int messageId, int scope, int byUserId)
        {
            if (messageId <= 0) return false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var chk = new MySqlCommand(
                    "SELECT 1 FROM pinned_messages WHERE message_id=@m AND scope=@s", conn))
                {
                    chk.Parameters.AddWithValue("@m", messageId);
                    chk.Parameters.AddWithValue("@s", scope);
                    if (chk.ExecuteScalar() != null)
                    {
                        using var del = new MySqlCommand(
                            "DELETE FROM pinned_messages WHERE message_id=@m AND scope=@s", conn);
                        del.Parameters.AddWithValue("@m", messageId);
                        del.Parameters.AddWithValue("@s", scope);
                        del.ExecuteNonQuery();
                        return false;
                    }
                }
                using (var ins = new MySqlCommand(
                    "INSERT IGNORE INTO pinned_messages (message_id, scope, pinned_by) VALUES (@m, @s, @u)", conn))
                {
                    ins.Parameters.AddWithValue("@m", messageId);
                    ins.Parameters.AddWithValue("@s", scope);
                    ins.Parameters.AddWithValue("@u", byUserId);
                    ins.ExecuteNonQuery();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Множество id закреплённых сообщений для диапазона (для отметки в рендере).</summary>
        public static HashSet<int> PinnedIds(int scope)
        {
            var set = new HashSet<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT message_id FROM pinned_messages WHERE scope=@s", conn);
                cmd.Parameters.AddWithValue("@s", scope);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(Convert.ToInt32(r["message_id"]));
            }
            catch { }
            return set;
        }

        /// <summary>Закреплённые в личном чате (между me и partner).</summary>
        public static List<PinnedItem> ForDirect(int me, int partner)
        {
            var list = new List<PinnedItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.id, m.text, TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender, u.login " +
                    "FROM pinned_messages p JOIN messages m ON m.id=p.message_id " +
                    "JOIN users u ON u.id=m.sender_id " +
                    "WHERE p.scope=0 AND ((m.sender_id=@me AND m.receiver_id=@th) OR (m.sender_id=@th AND m.receiver_id=@me)) " +
                    "ORDER BY p.pinned_at DESC", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@th", partner);
                Fill(cmd, list);
            }
            catch { }
            return list;
        }

        /// <summary>Закреплённые в групповом чате.</summary>
        public static List<PinnedItem> ForGroup(int groupId)
        {
            var list = new List<PinnedItem>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT gm.id, gm.text, TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender, u.login " +
                    "FROM pinned_messages p JOIN group_messages gm ON gm.id=p.message_id " +
                    "JOIN users u ON u.id=gm.sender_id " +
                    "WHERE p.scope=1 AND gm.group_id=@g ORDER BY p.pinned_at DESC", conn);
                cmd.Parameters.AddWithValue("@g", groupId);
                Fill(cmd, list);
            }
            catch { }
            return list;
        }

        private static void Fill(MySqlCommand cmd, List<PinnedItem> list)
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string sender = r["sender"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(sender)) sender = r["login"].ToString();
                list.Add(new PinnedItem
                {
                    MessageId = Convert.ToInt32(r["id"]),
                    Sender = sender,
                    TextCipher = r["text"]?.ToString() ?? ""
                });
            }
        }
    }
}
