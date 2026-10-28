// ============================================================
//  QuoteText — источник ответа и пересылки
// ============================================================
//
//  Одно место на ЛС, группы и каналы: как назвать сообщение в цитате
//  ответа и как показать пометку «Переслано от…».
//
//  Раньше у каждого чата был свой способ, и у каждого своя дыра. Цитата
//  ответа на фото в канале выглядела «↩ : » — без имени и без текста; ответ
//  на альбом называл его просто фото; а пометка пересылки шла строкой
//  текста с переводом строки на конце, и у пересланного снимка без подписи
//  под пометкой оставалась пустая строка.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    internal static class QuoteText
    {
        /// <summary>
        /// Разбирает пометку пересылки: «↪ Переслано от Пети:\nтекст» →
        /// «↪ Переслано от Пети» и «текст». false — сообщение не пересланное.
        /// Понимает и старую запись в одну строку («↪ Переслано от Пети: текст»).
        /// </summary>
        public static bool SplitForward(string text, out string header, out string body)
        {
            header = null;
            body = text ?? "";
            if (string.IsNullOrEmpty(text)
                || !text.StartsWith(ForwardHelper.ForwardMark, StringComparison.Ordinal))
                return false;

            int nl = text.IndexOf('\n');
            string first;
            if (nl >= 0)
            {
                first = text.Substring(0, nl);
                body = text.Substring(nl + 1);
            }
            else
            {
                int colon = text.IndexOf(':', ForwardHelper.ForwardMark.Length);
                first = colon > 0 ? text.Substring(0, colon) : text;
                body = colon > 0 ? text.Substring(colon + 1) : "";
            }
            header = first.TrimEnd('\r', ' ', ':');
            body = body.Trim('\r', '\n', ' ');
            return true;
        }

        /// <summary>Текст без пометки пересылки и без знака пачки.</summary>
        public static string BodyOf(string text)
        {
            string t = Albums.Strip(text ?? "");
            if (SplitForward(t, out _, out var body)) t = body;
            return t.Trim();
        }

        /// <summary>
        /// Строка цитаты: текст сообщения, а если его нет — чем оно было. У
        /// вложения с подписью — и то и другое: «📷 Фото · подпись».
        /// </summary>
        public static string Describe(string text, bool hasImage, bool hasAudio, bool hasVideo,
                                      string fileName, bool album = false, bool deleted = false)
        {
            if (deleted) return "🚫 Сообщение удалено";
            string t = BodyOf(text);
            string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
            bool gif = t.StartsWith("gif:", StringComparison.OrdinalIgnoreCase) || ext == "gif";
            if (gif) t = "";

            string kind;
            if (album) kind = "🖼 Альбом";
            else if (gif) kind = "🎞 GIF";
            else if (hasVideo) kind = "⭕ Кружок";
            else if (MediaPlayerForm.IsVideo(ext)) kind = "🎥 Видео";
            else if (hasImage) kind = "📷 Фото";
            else if (hasAudio) kind = "🎤 Голосовое сообщение";
            else if (!string.IsNullOrWhiteSpace(fileName)) kind = "📎 " + Path.GetFileName(fileName);
            else kind = "";

            // Цитата — одна строка: переводы строк в ней только мешают.
            t = t.Replace("\r", " ").Replace('\n', ' ').Trim();
            if (kind.Length == 0) return t;
            return t.Length == 0 ? kind : kind + " · " + t;
        }

        /// <summary>
        /// Цитаты для набора сообщений одной таблицы — одним запросом.
        ///
        /// Кроме самого сообщения смотрим на СЛЕДУЮЩЕЕ того же автора в том же
        /// чате: если оно идёт со знаком пачки, источник — альбом. Отдельного
        /// столбца у альбома нет (см. Albums), а знак зашифрован, поэтому
        /// сравнить его можно только здесь, после расшифровки.
        /// </summary>
        /// <param name="sameChat">Условие «тот же чат» для x и m, например
        /// «x.group_id=m.group_id».</param>
        /// <param name="hasDeleted">Есть ли у таблицы is_deleted.</param>
        /// <param name="media">Есть ли у таблицы столбцы вложений.</param>
        public static Dictionary<int, (string text, string sender)> Load(
            MySqlConnection conn, string table, string sameChat, ICollection<int> ids,
            bool hasDeleted = true, bool media = true)
        {
            var map = new Dictionary<int, (string text, string sender)>();
            if (conn == null || ids == null || ids.Count == 0) return map;

            string mediaCols = media
                ? "m.file_name, (m.image_data IS NOT NULL) AS has_img, " +
                  "(m.audio_data IS NOT NULL) AS has_audio, (m.video_data IS NOT NULL) AS has_video, "
                : "NULL AS file_name, 0 AS has_img, 0 AS has_audio, 0 AS has_video, ";
            string delCol = hasDeleted ? "m.is_deleted" : "0";
            using var cmd = new MySqlCommand($@"
                SELECT m.id, m.text, {mediaCols}{delCol} AS del,
                       TRIM(CONCAT(u.Name,' ',u.Surname)) AS sname, u.login,
                       (SELECT x.text FROM {table} x
                         WHERE x.sender_id = m.sender_id AND {sameChat} AND x.id > m.id
                         ORDER BY x.id LIMIT 1) AS next_text
                FROM {table} m
                JOIN users u ON u.id = m.sender_id
                WHERE m.id IN ({string.Join(",", ids)})", conn);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                int id = Convert.ToInt32(rd["id"]);
                string text = "", next = "";
                try { text = Crypto.Dec(rd["text"] == DBNull.Value ? "" : rd["text"].ToString()); } catch { }
                try { next = Crypto.Dec(rd["next_text"] == DBNull.Value ? "" : rd["next_text"].ToString()); } catch { }
                string sender = rd["sname"] == DBNull.Value ? "" : rd["sname"].ToString().Trim();
                if (string.IsNullOrWhiteSpace(sender))
                    sender = rd["login"] == DBNull.Value ? "" : rd["login"].ToString();

                bool img = Flag(rd["has_img"]), aud = Flag(rd["has_audio"]), vid = Flag(rd["has_video"]);
                string fn = rd["file_name"] == DBNull.Value ? null : rd["file_name"].ToString();
                bool tile = img || MediaPlayerForm.IsVideo(
                    Path.GetExtension(fn ?? "").TrimStart('.').ToLowerInvariant());
                // Сам снимок пачки или первое вложение, за которым идёт пачка.
                bool album = tile && (Albums.IsPart(text) || Albums.IsPart(next));

                map[id] = (Describe(text, img, aud, vid, fn, album, Flag(rd["del"])), sender);
            }

            // Не нашлось — сообщение стёрто совсем. Так и пишем, а не молчим:
            // пустая цитата выглядит поломкой.
            foreach (int id in ids)
                if (!map.ContainsKey(id)) map[id] = ("🚫 Сообщение удалено", "");
            return map;
        }

        private static bool Flag(object v)
        {
            if (v == null || v == DBNull.Value) return false;
            try { return Convert.ToInt64(v) != 0; } catch { return false; }
        }
    }

    /// <summary>
    /// Цитата ответа в пузыре — как в Telegram: имя автора и одна строка с
    /// текстом или с тем, чем было сообщение. Рисуется сама, одним окном, и
    /// ровно по своему содержимому — без пустого места под строкой.
    /// </summary>
    internal sealed class QuoteBlock : Control
    {
        private static readonly Font NameFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        private static readonly Font LineFont = new Font("Segoe UI", 8.5f);
        private const int PadX = 8, PadY = 4;

        private string _sender;
        private string _line;
        private readonly Color _accent;
        private readonly Color _tint;
        private readonly int _maxWidth;

        /// <param name="accent">Цвет имени.</param>
        /// <param name="tint">Чем подкрашен фон цитаты относительно пузыря.</param>
        public QuoteBlock(string sender, string line, int maxWidth, Color back, Color accent, Color tint)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = back;
            Cursor = Cursors.Hand;
            _accent = accent;
            _tint = tint;
            _maxWidth = Math.Max(120, maxWidth);
            SetContent(sender, line);
        }

        /// <summary>Подставить имя и строку (цитата доехала позже пузыря).</summary>
        public void SetContent(string sender, string line)
        {
            _sender = string.IsNullOrWhiteSpace(sender) ? "" : sender.Trim();
            _line = string.IsNullOrWhiteSpace(line) ? "…" : line;
            int nameW = _sender.Length > 0 ? TextRenderer.MeasureText(_sender, NameFont).Width : 0;
            int lineW = TextRenderer.MeasureText(_line, LineFont).Width;
            int nameH = _sender.Length > 0 ? NameFont.Height : 0;
            Size = new Size(Math.Min(_maxWidth, Math.Max(120, Math.Max(nameW, lineW) + PadX * 2)),
                            PadY + nameH + LineFont.Height + PadY);
            Invalidate();
        }

        private static double Brightness(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static Color Blend(Color a, Color b, double k) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = BackColor;
            // Светлый пузырь (светлая тема) — тёмный текст, иначе светлый.
            bool light = Brightness(back) > 0.6;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(Blend(back, _tint, light ? 0.10 : 0.18)))
                g.FillRoundedRectangle(br, 0, 0, Width - 1, Height - 1, 6);

            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                        | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int y = PadY;
            if (_sender.Length > 0)
            {
                var nameColor = light ? Blend(_accent, Color.Black, 0.25) : _accent;
                TextRenderer.DrawText(g, _sender, NameFont,
                    new Rectangle(PadX, y, Width - PadX * 2, NameFont.Height), nameColor, flags);
                y += NameFont.Height;
            }
            var lineColor = light ? Color.FromArgb(79, 84, 92)
                : Brightness(back) > 0.35 ? Color.FromArgb(225, 228, 255) : Color.FromArgb(185, 190, 198);
            TextRenderer.DrawText(g, _line, LineFont,
                new Rectangle(PadX, y, Width - PadX * 2, LineFont.Height), lineColor, flags);
        }
    }
}
