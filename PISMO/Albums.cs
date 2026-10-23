// ============================================================
//  Albums — какие сообщения показывать одной плиткой
// ============================================================
//
//  Порт core/Albums.kt с телефона, слово в слово по смыслу: пачка вложений,
//  отправленная разом, собирается в один альбом.
//
//  Почему без отдельного столбца в базе: прав на ALTER у приложения нет (см.
//  DbMigrator), и «album_id» пришлось бы ждать от администратора, а до тех
//  пор плиток не было бы вовсе. Поэтому альбом УЗНАЁТСЯ по тем же признакам,
//  по которым его узнал бы человек: подряд идущие вложения одного
//  отправителя, где подпись есть только у первого. Ровно так пачка и уходит
//  из обоих клиентов, а заодно в плитки собирается и всё, отправленное
//  россыпью раньше.
//
//  Разрыв по времени нужен для больших роликов: пока такой уезжает на
//  сервер, соседний по пачке уже лежит в базе. Две минуты — с запасом больше
//  любой паузы внутри пачки и заметно меньше обычной паузы между отправками.
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;

namespace PISMO
{
    internal static class Albums
    {
        /// <summary>Больше десяти в одну плитку не кладём — как в Telegram.</summary>
        public const int Max = 10;

        private static readonly TimeSpan Gap = TimeSpan.FromSeconds(120);

        private static bool Flag(DataRow r, string col) =>
            r.Table.Columns.Contains(col) && r[col] != DBNull.Value && Convert.ToBoolean(r[col]);

        private static string FileNameOf(DataRow r) =>
            r.Table.Columns.Contains("file_name") && r["file_name"] != DBNull.Value
                ? r["file_name"].ToString() : null;

        /// <summary>Фото или видеофайл — то, из чего собирается плитка.</summary>
        public static bool IsTile(DataRow r)
        {
            if (r == null) return false;
            if (r.Table.Columns.Contains("is_deleted") && r["is_deleted"] != DBNull.Value
                && Convert.ToBoolean(r["is_deleted"])) return false;
            if (Flag(r, "has_img")) return true;
            if (!Flag(r, "has_file")) return false;
            string fn = FileNameOf(r);
            if (string.IsNullOrEmpty(fn)) return false;
            string ext = Path.GetExtension(fn).TrimStart('.').ToLowerInvariant();
            return MediaPlayerForm.IsVideo(ext);
        }

        private static int SenderOf(DataRow r) => Convert.ToInt32(r["sender_id"]);
        private static DateTime TimeOf(DataRow r) => Convert.ToDateTime(r["created_at"]);
        private static int ReplyOf(DataRow r) =>
            r.Table.Columns.Contains("reply_to_id") && r["reply_to_id"] != DBNull.Value
                ? Convert.ToInt32(r["reply_to_id"]) : 0;

        /// <summary>
        /// Сколько строк, начиная с <paramref name="start"/>, идут в ОДНУ плитку.
        /// Единица означает «обычное сообщение», плитки нет.
        /// </summary>
        /// <param name="solo">
        /// Сообщения, которые нельзя прятать в плитку: закреплённые и те, на
        /// которых висят реакции, — в плитке их негде показать.
        /// </param>
        public static int Length(IList<DataRow> rows, int start, Func<int, bool> solo)
        {
            if (rows == null || start < 0 || start >= rows.Count) return 1;
            var head = rows[start];
            if (!IsTile(head)) return 1;
            int headId = Convert.ToInt32(head["id"]);
            if (solo != null && solo(headId)) return 1;

            int take = 1;
            var prevTime = TimeOf(head);
            int sender = SenderOf(head);

            for (int i = start + 1; i < rows.Count && take < Max; i++)
            {
                var r = rows[i];
                if (!IsTile(r)) break;
                int id = Convert.ToInt32(r["id"]);
                if (solo != null && solo(id)) break;
                if (SenderOf(r) != sender) break;
                if (ReplyOf(r) != 0) break;
                // Своя подпись — это уже отдельное сообщение, а не продолжение
                // пачки: подпись бывает только у первого.
                string text = Crypto.Dec(r["text"].ToString());
                if (!string.IsNullOrWhiteSpace(text)) break;
                var t = TimeOf(r);
                if (t - prevTime > Gap) break;
                prevTime = t;
                take++;
            }

            return take;
        }
    }
}
