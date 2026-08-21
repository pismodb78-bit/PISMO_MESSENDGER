using System;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Пересылка сообщений МЕЖДУ любыми чатами (ЛС ↔ группа ↔ канал сервера)
    /// ВМЕСТЕ С МЕДИА: фото, GIF, голосовые, видео-кружки, видео, файлы.
    /// Копирование BLOB-ов делается прямо в SQL (INSERT … SELECT) — байты не
    /// гоняются через клиент.
    /// scope: 0 = messages (ЛС), 1 = group_messages, 2 = server_messages.
    /// </summary>
    public static class ForwardHelper
    {
        public static string TableOf(int scope) =>
            scope == 1 ? "group_messages" : scope == 2 ? "server_messages" : "messages";

        /// <summary>Текст с пометкой пересылки. GIF-ссылки ("gif:…") не префиксуем,
        /// иначе они перестанут рендериться как GIF.</summary>
        public static string DecorateText(string senderName, string plainText)
        {
            plainText ??= "";
            if (plainText.StartsWith("gif:", StringComparison.OrdinalIgnoreCase))
                return plainText;
            return string.IsNullOrWhiteSpace(senderName)
                ? $"↪ Переслано:\n{plainText}"
                : $"↪ Переслано от {senderName}:\n{plainText}";
        }

        /// <summary>
        /// Переслать сообщение srcScope/srcId в чат dstScope/targetId (receiver_id,
        /// group_id или channel_id). Медиа копируется SQL-ом; при отсутствии
        /// медиа-колонок в исходной таблице — откат на текстовую пересылку.
        /// Если srcId == 0 (источник неизвестен) — шлём только текст.
        /// </summary>
        public static void Forward(int srcScope, int srcId, string senderName, string plainText,
                                   int dstScope, int senderId, int targetId)
        {
            string newPlain = DecorateText(senderName, plainText);
            string newEnc = Crypto.Enc(newPlain);

            string dstCols = dstScope switch
            {
                0 => "sender_id, receiver_id",
                1 => "group_id, sender_id",
                _ => "channel_id, sender_id"
            };
            // Порядок значений (@sid/@tid) зависит от таблицы-приёмника.
            string dstVals = dstScope == 0 ? "@sid, @tid" : "@tid, @sid";

            using var conn = DBHelper.OpenConnection();

            if (srcId > 0)
            {
                try
                {
                    string src = TableOf(srcScope);
                    using var cmd = new MySqlCommand(
                        $"INSERT INTO {TableOf(dstScope)} ({dstCols}, text, image_data, audio_data, video_data, file_data, file_name) " +
                        $"SELECT {dstVals}, @txt, image_data, audio_data, video_data, file_data, file_name " +
                        $"FROM {src} WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@sid", senderId);
                    cmd.Parameters.AddWithValue("@tid", targetId);
                    cmd.Parameters.AddWithValue("@txt", newEnc);
                    cmd.Parameters.AddWithValue("@id", srcId);
                    if (cmd.ExecuteNonQuery() > 0) return;
                }
                catch
                {
                    // Старая схема без медиа-колонок — упадём на текстовый вариант ниже.
                }
            }

            using var txt = new MySqlCommand(
                $"INSERT INTO {TableOf(dstScope)} ({dstCols}, text) VALUES ({dstVals}, @txt)", conn);
            txt.Parameters.AddWithValue("@sid", senderId);
            txt.Parameters.AddWithValue("@tid", targetId);
            txt.Parameters.AddWithValue("@txt", newEnc);
            txt.ExecuteNonQuery();
        }
    }
}
