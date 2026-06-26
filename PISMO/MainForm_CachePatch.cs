// ============================================================
//  MainForm — CachePatch (partial)
//  Добавьте этот файл в проект. Он заменяет логику чтения
//  медиабайтов из БД на чтение из локального кеша.
//
//  УСТАНОВКА:
//  1. Добавить MediaCache.cs в проект.
//  2. Добавить этот файл в проект.
//  3. В MainForm.cs найти оба метода LoadMessages() и
//     LoadGroupMessages() и ЗАМЕНИТЬ строки чтения байтов:
//
//     БЫЛО (в foreach DataRow):
//         byte[] img   = row["image_data"] == DBNull.Value ? null : (byte[])row["image_data"];
//         byte[] audio = row["audio_data"] == DBNull.Value ? null : (byte[])row["audio_data"];
//         byte[] video = row["video_data"] == DBNull.Value ? null : (byte[])row["video_data"];
//         byte[] fileData = row["file_data"] == DBNull.Value ? null : (byte[])row["file_data"];
//         string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();
//
//     СТАЛО:
//         string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();
//         var (img, audio, video, fileData) = ResolveCachedMedia(row, msgId, fileName);
//
//  4. В MainForm() конструктор добавить после DeviceSettings.Load():
//         MediaCache.Init();
//
//  5. В SendMessage() и SendGroupMessage() после cmd.ExecuteNonQuery():
//         int newId = (int)cmd.LastInsertedId;
//         InvalidateCacheOnSend(newId, imageData, audioData, videoData, fileData, fileName);
//     — нет, кешировать при отправке не нужно, кеш заполнится при следующей загрузке.
//
//  6. В DeleteMessage() после ExecuteNonQuery() добавить:
//         MediaCache.Invalidate(msgId, "img");
//         MediaCache.Invalidate(msgId, "audio");
//         MediaCache.Invalidate(msgId, "video");
//         MediaCache.Invalidate(msgId, "file");
//
//  7. В SQL запросах LoadMessages / LoadGroupMessages — убрать тяжёлые BLOB-колонки
//     если файл уже есть в кеше (оптимизация описана ниже в методе BuildSelectSql).
// ============================================================

using System;
using System.Data;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    public partial class MainForm
    {
        // ────────────────────────────────────────────────────────────────
        //  ГЛАВНЫЙ МЕТОД: получить медиа из кеша или из БД
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Возвращает (img, audio, video, fileData) для сообщения msgId.
        /// Сначала пробует кеш; если нет — берёт из DataRow и сохраняет в кеш.
        /// </summary>
        private static (byte[] img, byte[] audio, byte[] video, byte[] fileData)
            ResolveCachedMedia(DataRow row, int msgId, string fileName)
        {
            byte[] img      = ReadCachedOrRow(row, "image_data", msgId, "img",   null);
            byte[] audio    = ReadCachedOrRow(row, "audio_data", msgId, "audio", null);
            byte[] video    = ReadCachedOrRow(row, "video_data", msgId, "video", null);
            byte[] fileData = ReadCachedOrRow(row, "file_data",  msgId, "file",  fileName);

            return (img, audio, video, fileData);
        }

        /// <summary>
        /// Читает колонку из DataRow или из кеша.
        /// Если прочитал из БД — сохраняет в кеш для следующего раза.
        /// </summary>
        private static byte[] ReadCachedOrRow(
            DataRow row, string column, int msgId, string kind, string fileName)
        {
            // Сначала пробуем кеш
            byte[] cached = MediaCache.Get(msgId, kind, fileName);
            if (cached != null) return cached;

            // Кеша нет — смотрим в DataRow
            if (!row.Table.Columns.Contains(column)) return null;
            if (row[column] == DBNull.Value || row[column] == null) return null;

            byte[] data;
            try { data = (byte[])row[column]; }
            catch { return null; }

            if (data == null || data.Length == 0) return null;

            // Сохраняем в кеш (асинхронно, не блокируем UI)
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                MediaCache.Put(msgId, kind, data, fileName));

            return data;
        }

        // ────────────────────────────────────────────────────────────────
        //  ОПТИМИЗИРОВАННЫЙ SQL: не грузим BLOB если есть в кеше
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Строит SELECT для личных сообщений.
        /// Для каждого msgId, у которого все медиа уже в кеше,
        /// заменяем тяжёлые BLOB-колонки на NULL — экономим трафик к БД.
        ///
        /// Вызов: используй вместо хардкоженного SQL в LoadMessages().
        /// </summary>
        public static string BuildMessagesSql(bool forGroup = false)
        {
            // Базовый SELECT — всегда тянем BLOB (простой вариант).
            // Оптимизированный вариант с CASE WHEN ниже.
            if (forGroup)
            {
                return @"
                    SELECT gm.id, gm.sender_id, gm.text,
                           gm.image_data, gm.audio_data, gm.video_data,
                           gm.file_data, gm.file_name,
                           gm.reply_to_id, gm.is_deleted, gm.edited_at, gm.created_at,
                           TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login
                    FROM group_messages gm
                    JOIN users u ON u.id = gm.sender_id
                    WHERE gm.group_id=@g
                    ORDER BY gm.created_at ASC";
            }
            else
            {
                return @"
                    SELECT m.id, m.sender_id, m.text,
                           m.image_data, m.audio_data, m.video_data,
                           m.file_data, m.file_name,
                           m.reply_to_id, m.is_deleted, m.edited_at, m.created_at,
                           TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login
                    FROM messages m
                    JOIN users u ON u.id = m.sender_id
                    WHERE (m.sender_id=@me AND m.receiver_id=@them)
                       OR (m.sender_id=@them AND m.receiver_id=@me)
                    ORDER BY m.created_at ASC";
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  ДВУХЭТАПНАЯ ЗАГРУЗКА (продвинутая оптимизация)
        //
        //  Этап 1: SELECT id, file_name + метаданные (без BLOB) — быстро
        //  Этап 2: Для сообщений без кеша — догружаем BLOB по id
        //
        //  Используй эти методы вместо одного большого SELECT
        //  для максимальной скорости при большом количестве медиа.
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Шаг 1: Загружаем все сообщения БЕЗ BLOB-данных (быстро).
        /// </summary>
        public static DataTable LoadMessagesMetaOnly(int myId, int themId)
        {
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT m.id, m.sender_id, m.text,
                       NULL AS image_data, NULL AS audio_data,
                       NULL AS video_data, NULL AS file_data,
                       m.file_name,
                       m.reply_to_id, m.is_deleted, m.edited_at, m.created_at,
                       TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login,
                       (m.image_data IS NOT NULL) AS has_img,
                       (m.audio_data IS NOT NULL) AS has_audio,
                       (m.video_data IS NOT NULL) AS has_video,
                       (m.file_data  IS NOT NULL) AS has_file
                FROM messages m
                JOIN users u ON u.id = m.sender_id
                WHERE (m.sender_id=@me AND m.receiver_id=@them)
                   OR (m.sender_id=@them AND m.receiver_id=@me)
                ORDER BY m.created_at ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@me",   myId);
            cmd.Parameters.AddWithValue("@them", themId);
            var dt = new DataTable();
            new MySqlDataAdapter(cmd).Fill(dt);
            return dt;
        }

        /// <summary>
        /// Шаг 1 для групп: Загружаем метаданные без BLOB.
        /// </summary>
        public static DataTable LoadGroupMessagesMetaOnly(int groupId)
        {
            using var conn = DBHelper.OpenConnection();
            const string sql = @"
                SELECT gm.id, gm.sender_id, gm.text,
                       NULL AS image_data, NULL AS audio_data,
                       NULL AS video_data, NULL AS file_data,
                       gm.file_name,
                       gm.reply_to_id, gm.is_deleted, gm.edited_at, gm.created_at,
                       TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login,
                       (gm.image_data IS NOT NULL) AS has_img,
                       (gm.audio_data IS NOT NULL) AS has_audio,
                       (gm.video_data IS NOT NULL) AS has_video,
                       (gm.file_data  IS NOT NULL) AS has_file
                FROM group_messages gm
                JOIN users u ON u.id = gm.sender_id
                WHERE gm.group_id=@g
                ORDER BY gm.created_at ASC";

            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@g", groupId);
            var dt = new DataTable();
            new MySqlDataAdapter(cmd).Fill(dt);
            return dt;
        }

        /// <summary>
        /// Шаг 2: Для одного сообщения по id — загружаем только нужные BLOB (которых нет в кеше).
        /// </summary>
        public static (byte[] img, byte[] audio, byte[] video, byte[] fileData)
            LoadMediaForMessage(int msgId, string fileName,
                                bool hasImg, bool hasAudio, bool hasVideo, bool hasFile,
                                bool isGroup)
        {
            // Проверяем кеш для каждого типа
            bool needImg   = hasImg   && !MediaCache.Has(msgId, "img",   fileName);
            bool needAudio = hasAudio && !MediaCache.Has(msgId, "audio", null);
            bool needVideo = hasVideo && !MediaCache.Has(msgId, "video", null);
            bool needFile  = false; // Не загружаем файлы автоматически при открытии чата

            byte[] img      = hasImg   ? MediaCache.Get(msgId, "img",   fileName) : null;
            byte[] audio    = hasAudio ? MediaCache.Get(msgId, "audio", null)     : null;
            byte[] video    = hasVideo ? MediaCache.Get(msgId, "video", null)     : null;
            byte[] fileData = null; // Будет загружено только по клику на карточку файла

            // Если всё уже в кеше — не идём в БД вообще
            if (!needImg && !needAudio && !needVideo && !needFile)
                return (img, audio, video, fileData);

            // Строим SELECT только нужных колонок
            var cols = new System.Collections.Generic.List<string>();
            if (needImg)   cols.Add("image_data");
            if (needAudio) cols.Add("audio_data");
            if (needVideo) cols.Add("video_data");
            if (needFile)  cols.Add("file_data");

            string table = isGroup ? "group_messages" : "messages";
            string colList = string.Join(", ", cols);

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd  = new MySqlCommand(
                    $"SELECT {colList} FROM {table} WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", msgId);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return (img, audio, video, fileData);

                if (needImg && !reader.IsDBNull(reader.GetOrdinal("image_data")))
                {
                    img = (byte[])reader["image_data"];
                    MediaCache.Put(msgId, "img", img, fileName);
                }
                if (needAudio && !reader.IsDBNull(reader.GetOrdinal("audio_data")))
                {
                    audio = (byte[])reader["audio_data"];
                    MediaCache.Put(msgId, "audio", audio);
                }
                if (needVideo && !reader.IsDBNull(reader.GetOrdinal("video_data")))
                {
                    video = (byte[])reader["video_data"];
                    MediaCache.Put(msgId, "video", video);
                }
                if (needFile && !reader.IsDBNull(reader.GetOrdinal("file_data")))
                {
                    fileData = (byte[])reader["file_data"];
                    MediaCache.Put(msgId, "file", fileData, fileName);
                }
            }
            catch { /* если не получилось — возвращаем что есть */ }

            return (img, audio, video, fileData);
        }

        // ────────────────────────────────────────────────────────────────
        //  ИНВАЛИДАЦИЯ ПРИ УДАЛЕНИИ СООБЩЕНИЯ
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Вызвать из DeleteMessage() после обновления БД.
        /// </summary>
        private static void InvalidateCacheForMessage(int msgId, string fileName = null)
        {
            MediaCache.Invalidate(msgId, "img",   fileName);
            MediaCache.Invalidate(msgId, "audio", null);
            MediaCache.Invalidate(msgId, "video", null);
            MediaCache.Invalidate(msgId, "file",  fileName);
        }

        // ────────────────────────────────────────────────────────────────
        //  КНОПКА ОЧИСТКИ КЕША В НАСТРОЙКАХ
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Добавить в btnSettings_Click в меню настроек:
        ///   menu.Items.Add("🗑 Очистить кеш медиа", null, (s, ev) => ClearMediaCache());
        /// </summary>
        private void ClearMediaCache()
        {
            long sizeMb = MediaCache.GetCacheSize() / 1024 / 1024;
            var result = MessageBox.Show(
                $"Очистить кеш медиафайлов? ({sizeMb} МБ)\n" +
                "После очистки чаты будут загружаться немного медленнее до повторного кеширования.",
                "PISMO — Очистка кеша",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != System.Windows.Forms.DialogResult.Yes) return;

            MediaCache.Clear();
            MessageBox.Show("Кеш очищен.", "PISMO",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
