using System;
using System.Collections.Generic;
using System.Text;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Упоминания в каналах серверов (таблица server_mentions, миграция 15).
    ///
    /// Зачем отдельная таблица: текст сообщений лежит в БД ЗАШИФРОВАННЫМ
    /// (Crypto.Enc → AES-GCM в Base64), поэтому искать «@логин» запросом
    /// LIKE по server_messages.text невозможно в принципе — в алфавите Base64
    /// нет даже символа «@». Раньше красная цифра упоминаний считалась именно
    /// так и не срабатывала никогда.
    ///
    /// Теперь адресаты вычисляются ОДИН РАЗ при отправке, пока открытый текст на
    /// руках, и складываются строками (message_id, channel_id, user_id). Бейдж
    /// после этого — обычный COUNT по индексу: не зависит ни от шифрования, ни от
    /// клиента, и одинаково работает на ПК и в мобильной версии.
    /// </summary>
    public static class ServerMentions
    {
        private static bool _tableOk = true;

        /// <summary>Таблица доступна? (false — миграция 15 ещё не применена.)</summary>
        public static bool Available => _tableOk;

        /// <summary>Разбирает «@…» в тексте и записывает упоминания. Вызывать сразу
        /// после вставки сообщения, с ОТКРЫТЫМ текстом и id вставленной строки.</summary>
        public static void Record(MySqlConnection conn, long messageId, int channelId,
                                  int senderId, string plainText)
        {
            if (!_tableOk || conn == null || messageId <= 0 || channelId <= 0) return;
            if (string.IsNullOrEmpty(plainText) || plainText.IndexOf('@') < 0) return;

            try
            {
                string lower = plainText.ToLowerInvariant();
                bool all = lower.Contains("@все") || lower.Contains("@all") || lower.Contains("@everyone");

                // Кандидаты — участники сервера, которому принадлежит канал, вместе с
                // логином и названием роли. Сравниваем вхождением подстроки, а не
                // разбором на слова: названия ролей бывают из нескольких слов, и
                // прежняя логика (LIKE '%@роль%') вела себя именно так.
                var targets = new List<int>();
                using (var cmd = new MySqlCommand(
                    "SELECT m.user_id, u.login, r.name AS role_name " +
                    "FROM server_channels sc " +
                    "JOIN server_members m ON m.server_id = sc.server_id " +
                    "JOIN users u ON u.id = m.user_id " +
                    "LEFT JOIN server_roles r ON r.id = m.role_id " +
                    "WHERE sc.id = @c", conn))
                {
                    cmd.Parameters.AddWithValue("@c", channelId);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        int uid = Convert.ToInt32(rd["user_id"]);
                        if (uid == senderId) continue;         // сам себя не упоминаешь
                        if (all) { targets.Add(uid); continue; }

                        string login = rd["login"]?.ToString() ?? "";
                        if (login.Length > 0 && lower.Contains("@" + login.ToLowerInvariant()))
                        { targets.Add(uid); continue; }

                        string role = rd["role_name"] == DBNull.Value ? "" : rd["role_name"].ToString();
                        if (!string.IsNullOrWhiteSpace(role) && lower.Contains("@" + role.ToLowerInvariant()))
                            targets.Add(uid);
                    }
                }   // читатель закрыт — можно выполнять вставку на этом же соединении

                if (targets.Count == 0) return;

                var sb = new StringBuilder("INSERT IGNORE INTO server_mentions (message_id, channel_id, user_id) VALUES ");
                using var ins = new MySqlCommand();
                ins.Connection = conn;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("(@m,@c,@u").Append(i).Append(')');
                    ins.Parameters.AddWithValue("@u" + i, targets[i]);
                }
                ins.Parameters.AddWithValue("@m", messageId);
                ins.Parameters.AddWithValue("@c", channelId);
                ins.CommandText = sb.ToString();
                ins.ExecuteNonQuery();
            }
            catch (MySqlException mex)
            {
                if (mex.Number == 1146) _tableOk = false;   // таблицы нет — миграция не применена
            }
            catch { }
        }

        /// <summary>То же, но открывает соединение само (для путей, где его нет под рукой).</summary>
        public static void Record(long messageId, int channelId, int senderId, string plainText)
        {
            if (!_tableOk || messageId <= 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                Record(conn, messageId, channelId, senderId, plainText);
            }
            catch { }
        }
    }
}
