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

        /// <summary>
        /// С какого момента пачки ходят со знаком (24 октября 2026, 11:00 UTC —
        /// выпуск, в котором он появился). Всё, что отправлено раньше, знака не
        /// несёт и никогда не понесёт, поэтому для старых сообщений оставлено
        /// прежнее узнавание по времени: иначе собранные когда-то плитки
        /// рассыпались бы на одиночные снимки.
        /// </summary>
        private static readonly DateTime MarkSince = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc);

        /// <summary>Разрыв по времени — только для старых сообщений.</summary>
        private static readonly TimeSpan Gap = TimeSpan.FromSeconds(120);

        private static DateTime TimeOf(DataRow r) => Convert.ToDateTime(r["created_at"]);

        /// <summary>Старое сообщение — то, что отправлено до появления знака.</summary>
        private static bool Legacy(DataRow r)
        {
            try { return TimeOf(r).ToUniversalTime() < MarkSince; }
            catch { return false; }
        }

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
            bool headLegacy = Legacy(head);
            var prevTime = TimeOf(head);

            for (int i = start + 1; i < rows.Count && take < Max; i++)
            {
                var r = rows[i];
                if (!IsTile(r)) break;
                int id = Convert.ToInt32(r["id"]);
                if (solo != null && solo(id)) break;
                if (SenderOf(r) != sender) break;
                if (ReplyOf(r) != 0) break;

                // В плитку идёт ОТМЕЧЕННОЕ пачкой. Отправленное по одному
                // отметки не несёт и остаётся отдельным сообщением, даже если
                // ушло секунда в секунду.
                string text = Crypto.Dec(r["text"].ToString());
                bool part = IsPart(text);

                // У старых сообщений знака нет и быть не может — для них
                // прежнее узнавание: без подписи и не дальше двух минут.
                bool legacy = headLegacy && Legacy(r)
                              && string.IsNullOrWhiteSpace(text)
                              && TimeOf(r) - prevTime <= Gap;

                if (!part && !legacy) break;
                prevTime = TimeOf(r);
                take++;
            }

            return take;
        }

        /// <summary>
        /// Подпись каждого снимка и ролика страницы для ПРОСМОТРА: у пачки она
        /// одна на всех — та, что ушла с первым вложением. Листая альбом,
        /// человек видит её на каждом кадре, а не только на первом.
        /// </summary>
        public static Dictionary<int, string> Captions(DataTable dt)
        {
            var map = new Dictionary<int, string>();
            if (dt == null) return map;
            var rows = new List<DataRow>(dt.Rows.Count);
            foreach (DataRow r in dt.Rows) rows.Add(r);
            for (int i = 0; i < rows.Count;)
            {
                int len = 1;
                string cap = "";
                try
                {
                    len = Math.Max(1, Length(rows, i, null));
                    cap = Strip(Crypto.Dec(rows[i]["text"] == DBNull.Value ? "" : rows[i]["text"].ToString())).Trim();
                }
                catch { }
                for (int k = 0; k < len && i + k < rows.Count; k++)
                {
                    try { map[Convert.ToInt32(rows[i + k]["id"])] = cap; } catch { }
                }
                i += len;
            }
            return map;
        }

        /// <summary>
        /// То же для готового списка вложений (лента просмотра по всей
        /// переписке): строки пачки берут подпись у её первого.
        /// </summary>
        public static Dictionary<int, string> CaptionsOf(
            IList<(int Id, int Sender, DateTime When, string Text)> rows)
        {
            var map = new Dictionary<int, string>();
            if (rows == null) return map;
            (int Id, int Sender, DateTime When, string Text)? head = null, prev = null;
            string headCap = "";
            foreach (var r in rows)
            {
                bool part = false;
                if (head is { } h && prev is { } p)
                {
                    bool legacy = r.When.ToUniversalTime() < MarkSince && h.When.ToUniversalTime() < MarkSince
                                  && string.IsNullOrWhiteSpace(r.Text) && r.When - p.When <= Gap;
                    part = r.Sender == h.Sender && (IsPart(r.Text) || legacy);
                }
                if (!part)
                {
                    head = r;
                    headCap = Strip(r.Text ?? "").Trim();
                }
                map[r.Id] = headCap;
                prev = r;
            }
            return map;
        }
    }
}
