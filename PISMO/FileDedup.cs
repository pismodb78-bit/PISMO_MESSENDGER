using System;
using System.Security.Cryptography;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Один и тот же файл не заливается на сервер дважды.
    ///
    /// ЗАЧЕМ. Отправить одно видео двум собеседникам значило залить его на
    /// сервер два раза — второй раз ровно так же долго, как первый, хотя те же
    /// байты там уже лежат. Теперь у вложения считается отпечаток (SHA-256), и
    /// если такой файл уже есть среди СВОИХ отправленных, новое сообщение
    /// собирается запросом INSERT … SELECT: база копирует содержимое у себя
    /// внутри, по сети не уходит ни байта. Отправка становится мгновенной.
    ///
    /// ЧЕГО ЭТО НЕ ДЕЛАЕТ. Места на сервере это не экономит: у каждого
    /// сообщения по-прежнему своя копия. Настоящая ссылка на общее тело — это
    /// отдельная таблица с телами файлов и переписанные запросы чтения во всех
    /// трёх таблицах сообщений, плюс подсчёт ссылок при удалении. Здесь
    /// сознательно выбрана та половина, которая убирает ожидание, не трогая
    /// чтение вовсе.
    ///
    /// ДОНОРА ИЩЕМ ТОЛЬКО СРЕДИ СВОИХ. Не из вежливости: копировать чужое
    /// вложение по совпадению отпечатка значило бы сообщать отправителю, что
    /// такой файл на сервере у кого-то уже есть, — по одной лишь скорости
    /// отправки. Своё же вложение он и так держит в руках.
    /// </summary>
    internal static class FileDedup
    {
        /// <summary>-1 — ещё не спрашивали, 0 — столбца нет, 1 — есть.</summary>
        private static int _supported = -1;

        internal readonly record struct Donor(string Table, long Id);

        internal static string Sha256Hex(byte[] data)
        {
            if (data == null || data.LongLength == 0) return null;
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        /// <summary>
        /// Есть ли в базе столбец file_sha. Прав ALTER у учётной записи
        /// приложения может не быть, и тогда миграция не применилась — это не
        /// ошибка, просто отправка идёт по-старому.
        /// </summary>
        internal static bool Supported(MySqlConnection conn)
        {
            if (_supported >= 0) return _supported == 1;
            try
            {
                using var cmd = new MySqlCommand("SELECT file_sha FROM messages LIMIT 0", conn);
                cmd.ExecuteNonQuery();
                _supported = 1;
            }
            catch { _supported = 0; }
            return _supported == 1;
        }

        /// <summary>Своё же сообщение, в котором это вложение уже лежит.</summary>
        internal static Donor? FindDonor(MySqlConnection conn, int me, string sha)
        {
            if (string.IsNullOrEmpty(sha) || me <= 0) return null;
            try
            {
                const string sql =
                    "(SELECT 'messages' AS t, id FROM messages " +
                    "  WHERE sender_id=@me AND file_sha=@sha AND file_data IS NOT NULL LIMIT 1) " +
                    "UNION ALL " +
                    "(SELECT 'group_messages', id FROM group_messages " +
                    "  WHERE sender_id=@me AND file_sha=@sha AND file_data IS NOT NULL LIMIT 1) " +
                    "UNION ALL " +
                    "(SELECT 'server_messages', id FROM server_messages " +
                    "  WHERE sender_id=@me AND file_sha=@sha AND file_data IS NOT NULL LIMIT 1) " +
                    "LIMIT 1";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@sha", sha);
                using var rd = cmd.ExecuteReader();
                if (!rd.Read()) return null;
                return new Donor(rd.GetString(0), Convert.ToInt64(rd[1]));
            }
            catch { return null; }
        }

        /// <summary>
        /// Вставляет сообщение, взяв тело файла у донора. Возвращает id новой
        /// строки или 0, если не получилось (тогда зовущий льёт как обычно).
        /// </summary>
        internal static long InsertCopy(MySqlConnection conn, bool isGroup, int target, int me,
            string encText, byte[] img, byte[] aud, byte[] vid, string fileName, string sha,
            Donor donor)
        {
            try
            {
                // Имя таблицы-донора подставляется в текст запроса, но взято
                // оно из нашего же перечня выше, а не из чужих данных.
                string sql = isGroup
                    ? "INSERT INTO group_messages (group_id, sender_id, text, image_data, audio_data, " +
                      "video_data, file_data, file_name, file_sha) " +
                      $"SELECT @g, @s, @t, @img, @aud, @vid, d.file_data, @fn, @sha FROM {donor.Table} d WHERE d.id=@did"
                    : "INSERT INTO messages (sender_id, receiver_id, text, image_data, audio_data, " +
                      "video_data, file_data, file_name, file_sha) " +
                      $"SELECT @s, @r, @t, @img, @aud, @vid, d.file_data, @fn, @sha FROM {donor.Table} d WHERE d.id=@did";

                using var cmd = new MySqlCommand(sql, conn);
                if (isGroup) { cmd.Parameters.AddWithValue("@g", target); cmd.Parameters.AddWithValue("@s", me); }
                else { cmd.Parameters.AddWithValue("@s", me); cmd.Parameters.AddWithValue("@r", target); }
                cmd.Parameters.AddWithValue("@t", encText ?? "");
                AddBlobParam(cmd, "@img", img);
                AddBlobParam(cmd, "@aud", aud);
                AddBlobParam(cmd, "@vid", vid);
                cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sha", sha);
                cmd.Parameters.AddWithValue("@did", donor.Id);
                // Копирование идёт внутри базы, но двести мегабайт всё равно
                // надо записать на диск — время на это нужно.
                cmd.CommandTimeout = 600;
                if (cmd.ExecuteNonQuery() <= 0) return 0;   // донора успели удалить
                return cmd.LastInsertedId;
            }
            catch { return 0; }
        }

        /// <summary>Проставляет отпечаток уже залитому вложению.</summary>
        internal static void StampSha(MySqlConnection conn, string table, long id, string sha)
        {
            if (string.IsNullOrEmpty(sha) || id <= 0) return;
            try
            {
                using var cmd = new MySqlCommand($"UPDATE {table} SET file_sha=@sha WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@sha", sha);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        private static void AddBlobParam(MySqlCommand cmd, string name, byte[] data)
        {
            if (data != null) cmd.Parameters.Add(name, MySqlDbType.LongBlob).Value = data;
            else cmd.Parameters.Add(name, MySqlDbType.LongBlob).Value = DBNull.Value;
        }
    }
}
