// ============================================================
//  Albums — какие сообщения показывать одной плиткой
// ============================================================
//
//  Порт core/Albums.kt с телефона, слово в слово по смыслу: пачка вложений,
//  отправленная разом, собирается в один альбом.
//
//  Почему без отдельного столбца в базе: прав на ALTER у приложения нет (см.
//  DbMigrator), и «album_id» пришлось бы ждать от администратора. Поэтому
//  пачка помечает САМА СЕБЯ: все вложения, кроме первого, уходят с невидимым
//  знаком вместо текста (U+2063, «невидимый разделитель»). Он ничего не
//  весит, ничего не показывает и ничего не ломает у клиента, который о нём
//  не знает, — там это просто сообщение без подписи.
//
//  Раньше пачка угадывалась по времени: подряд, один отправитель, без
//  подписи и не дальше двух минут. Угадывание ошибалось в обе стороны —
//  снимки, отправленные ПО ОДНОМУ, слипались в плитку, а пачка с медленным
//  роликом распадалась. Теперь догадок нет.
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

        /// <summary>
        /// Знак «это продолжение пачки» — невидимый разделитель U+2063. Стоит
        /// вместо текста у всех вложений пачки, кроме первого.
        /// </summary>
        public const string Mark = "\u2063";

        /// <summary>Пришло ли сообщение продолжением пачки.</summary>
        public static bool IsPart(string text) =>
            !string.IsNullOrEmpty(text) && text.Trim() == Mark;

        /// <summary>Текст для показа: знак пачки — это не текст.</summary>
        public static string Strip(string text) => IsPart(text) ? "" : text;

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
            int sender = SenderOf(head);

            for (int i = start + 1; i < rows.Count && take < Max; i++)
            {
                var r = rows[i];
                if (!IsTile(r)) break;
                int id = Convert.ToInt32(r["id"]);
                if (solo != null && solo(id)) break;
                if (SenderOf(r) != sender) break;
                if (ReplyOf(r) != 0) break;
                // В плитку идёт только ОТМЕЧЕННОЕ пачкой. Отправленное по
                // одному отметки не несёт и остаётся отдельным сообщением,
                // даже если ушло секунда в секунду.
                if (!IsPart(Crypto.Dec(r["text"].ToString()))) break;
                take++;
            }

            return take;
        }
    }
}
