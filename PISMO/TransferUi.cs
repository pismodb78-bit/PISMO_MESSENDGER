using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Как выглядят передачи файлов — в одном месте.
    ///
    /// ЗАЧЕМ ОБЩИЙ КОД. Полос теперь три: кружок в шапке списка чатов (там
    /// видно всё разом), полоса под перепиской и такая же полоса под каналом
    /// сервера. Рисовать их по отдельности означало бы три слегка разных вида
    /// одного и того же — на телефоне это уже один вид, и расходиться им
    /// незачем.
    ///
    /// Рисуем сами, без контролов на строку. Строки меняются несколько раз в
    /// секунду, и набор контролов пришлось бы пересобирать так же часто: это
    /// мигание, мусор в памяти и потерянный фокус поля ввода.
    /// </summary>
    internal static class TransferUi
    {
        internal const int RowH = 56;

        private static readonly Color Card = Color.FromArgb(43, 45, 49);
        private static readonly Color CardEdge = Color.FromArgb(58, 61, 66);
        private static readonly Color Up = Color.FromArgb(88, 101, 242);
        private static readonly Color Down = Color.FromArgb(59, 165, 93);
        private static readonly Color Bad = Color.FromArgb(237, 102, 99);
        private static readonly Color Grey = Color.FromArgb(128, 131, 138);
        private static readonly Color TextMain = Color.FromArgb(230, 232, 236);
        private static readonly Color TextMuted = Color.FromArgb(152, 155, 162);
        private static readonly Color Track = Color.FromArgb(62, 65, 71);

        /// <summary>Высота под столько строк. Ноль строк — ноль высоты.</summary>
        internal static int HeightFor(int rows) => rows <= 0 ? 0 : rows * RowH + 6;

        internal static Color AccentOf(Transfers.Item it) =>
            it.Error != null ? Bad
            : it.Ended && it.WasCancelled ? Grey
            : it.Upload ? Up : Down;

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using var path = Round(r, rad);
            using var br = new SolidBrush(c);
            g.FillPath(br, path);
        }

        private static RectangleF CardRect(int width, int row) =>
            new(8, 3 + row * RowH + 3, Math.Max(80, width - 16), RowH - 10);

        private static readonly StringFormat Mid = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        /// <summary>
        /// Рисует строки передач. [maxRows] — сколько влезает; остальные
        /// сворачиваются в «…и ещё N», иначе список вытеснил бы переписку.
        /// </summary>
        internal static void PaintRows(Graphics g, int width, Transfers.Item[] items, int maxRows)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var fName = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            using var fSmall = new Font("Segoe UI", 8f);
            using var fGlyph = new Font("Segoe UI Symbol", 10f);
            using var fBtn = new Font("Segoe UI Symbol", 9.5f);
            using var brName = new SolidBrush(TextMain);
            using var brMuted = new SolidBrush(TextMuted);

            int shown = Math.Min(items.Length, maxRows);
            for (int i = 0; i < shown; i++)
            {
                var it = items[i];
                var card = CardRect(width, i);
                var accent = AccentOf(it);

                Fill(g, card, 10, Card);
                using (var pen = new Pen(CardEdge))
                using (var edge = Round(card, 10))
                    g.DrawPath(pen, edge);

                // Кружок с направлением. Законченное помечаем исходом, а не
                // стрелкой: «галочка» и «крестик» читаются быстрее, чем текст.
                var chip = new RectangleF(card.X + 8, card.Y + 9, 30, 30);
                using (var brChip = new SolidBrush(Color.FromArgb(it.Ended ? 60 : 90, accent)))
                    g.FillEllipse(brChip, chip);
                string glyph = it.Ended
                    ? (it.Error != null ? "!" : it.WasCancelled ? "✕" : "✓")
                    : (it.Upload ? "⬆" : "⬇");
                using (var brGl = new SolidBrush(accent))
                    g.DrawString(glyph, fGlyph, brGl, chip, Mid);

                float textX = chip.Right + 10;
                bool retry = it.CanRetry;
                float btnsW = retry ? 62 : 34;
                float textW = Math.Max(40, card.Right - 10 - btnsW - textX);

                // Имя обрезаем сами: длинное вылезало бы на кнопки.
                string name = it.Name ?? "файл";
                if (g.MeasureString(name, fName).Width > textW)
                {
                    while (name.Length > 4 && g.MeasureString(name + "…", fName).Width > textW)
                        name = name[..^1];
                    name += "…";
                }
                g.DrawString(name, fName, brName, textX, card.Y + 5);

                using (var brStat = new SolidBrush(it.Error != null ? Bad : TextMuted))
                    g.DrawString(Shorten(g, it.Status, fSmall, textW), fSmall, brStat,
                                 textX, card.Y + 21);

                // Полоса. Долю знаем не всегда: скачивание из базы идёт одним
                // запросом, и делить там нечего — вместо обмана бежит отрезок.
                var bar = new RectangleF(textX, card.Bottom - 11, textW, 5);
                Fill(g, bar, 2.5f, Track);
                double f = it.Fraction;
                if (it.Ended)
                {
                    if (it.Error == null && !it.WasCancelled) Fill(g, bar, 2.5f, accent);
                }
                else if (f >= 0)
                {
                    float w = (float)(bar.Width * f);
                    if (w > 2) Fill(g, new RectangleF(bar.X, bar.Y, w, bar.Height), 2.5f, accent);
                }
                else
                {
                    float span = Math.Max(40, bar.Width / 4);
                    float travel = bar.Width + span;
                    float pos = (Environment.TickCount / 6f) % travel - span;
                    float x1 = Math.Max(bar.X, bar.X + pos);
                    float x2 = Math.Min(bar.Right, bar.X + pos + span);
                    if (x2 > x1) Fill(g, new RectangleF(x1, bar.Y, x2 - x1, bar.Height), 2.5f, accent);
                }

                // Кнопки. «Повторить» — только у сорванного и отменённого:
                // байты для этого держит сама передача.
                if (retry)
                {
                    var rb = RetryRect(card);
                    Fill(g, rb, 7, Color.FromArgb(60, 64, 72));
                    using var brR = new SolidBrush(Color.FromArgb(200, 210, 230));
                    g.DrawString("↻", fBtn, brR, rb, Mid);
                }
                var cb = CancelRect(card);
                Fill(g, cb, 7, it.Ended ? Color.FromArgb(54, 57, 63) : Color.FromArgb(74, 52, 56));
                using (var brX = new SolidBrush(it.Ended ? TextMuted : Color.FromArgb(240, 160, 160)))
                    g.DrawString("✕", fBtn, brX, cb, Mid);
            }

            if (items.Length > shown)
            {
                g.DrawString($"…и ещё {items.Length - shown}", fSmall, brMuted,
                             14, 3 + shown * RowH + 2);
            }
        }

        private static RectangleF CancelRect(RectangleF card) =>
            new(card.Right - 36, card.Y + (card.Height - 26) / 2, 26, 26);

        private static RectangleF RetryRect(RectangleF card) =>
            new(card.Right - 66, card.Y + (card.Height - 26) / 2, 26, 26);

        private static string Shorten(Graphics g, string s, Font f, float w)
        {
            if (string.IsNullOrEmpty(s) || g.MeasureString(s, f).Width <= w) return s ?? "";
            while (s.Length > 3 && g.MeasureString(s + "…", f).Width > w) s = s[..^1];
            return s + "…";
        }

        /// <summary>
        /// Нажатие по строкам. Возвращает true, если что-то сделали, — тогда
        /// вызывающему остаётся перерисоваться.
        ///
        /// Крестик у идущей передачи отменяет её, у законченной — закрывает
        /// строку. Раньше отмена просто вычёркивала строку, и человек не видел
        /// ни того, что отмена случилась, ни того, что её можно повторить.
        /// </summary>
        internal static bool Click(Point p, int width, Transfers.Item[] items, int maxRows)
        {
            int i = (p.Y - 3) / RowH;
            if (i < 0 || i >= Math.Min(items.Length, maxRows)) return false;
            var it = items[i];
            var card = CardRect(width, i);

            // Промах в пару пикселей мышью — норма, поэтому цели с запасом.
            var cb = CancelRect(card); cb.Inflate(4, 6);
            var rb = RetryRect(card); rb.Inflate(4, 6);

            if (it.CanRetry && rb.Contains(p)) { Transfers.Retry(it); return true; }
            if (cb.Contains(p))
            {
                if (it.Ended) Transfers.Dismiss(it); else Transfers.Cancel(it);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Кнопка-счётчик в шапке списка чатов.
        ///
        /// Раньше это была плоская кнопка с текстом «⇅ 3»: по ней нельзя было
        /// понять ни того, что именно идёт, ни того, как далеко оно ушло.
        /// Теперь видно и направление, и общую долю, и то, что что-то
        /// сорвалось.
        /// </summary>
        internal static void PaintBadge(Graphics g, Rectangle box, Transfers.Item[] items, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int up = 0, down = 0, bad = 0;
            long done = 0, total = 0;
            bool moving = false;
            foreach (var it in items)
            {
                if (it.Error != null) bad++;
                if (it.Ended) continue;
                if (it.Upload) up++; else down++;
                moving = true;
                if (it.Total > 0 && !it.Indeterminate) { done += it.Done; total += it.Total; }
            }

            var accent = bad > 0 && !moving ? Bad : up > 0 ? Up : down > 0 ? Down : Grey;
            var pill = new RectangleF(box.X, box.Y + (box.Height - 26) / 2f,
                                      box.Width, 26);
            Fill(g, pill, 13, Color.FromArgb(hover ? 70 : 46, accent));
            using (var pen = new Pen(Color.FromArgb(hover ? 190 : 120, accent)))
            using (var edge = Round(pill, 13))
                g.DrawPath(pen, edge);

            using var fGl = new Font("Segoe UI Symbol", 9f);
            using var fNum = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            using var brAcc = new SolidBrush(accent);
            using var brTxt = new SolidBrush(TextMain);

            string glyph = up > 0 && down > 0 ? "⇅" : up > 0 ? "⬆" : down > 0 ? "⬇" : "⇅";
            int n = up + down > 0 ? up + down : items.Length;
            var gl = new RectangleF(pill.X + 4, pill.Y, 16, pill.Height);
            g.DrawString(glyph, fGl, brAcc, gl, Mid);
            var num = new RectangleF(gl.Right, pill.Y, pill.Width - 20 - 4, pill.Height);
            g.DrawString(n.ToString(), fNum, brTxt, num, Mid);

            // Общая доля — тонкой чертой по низу: она отвечает на «долго ли
            // ещё» без открытия списка.
            if (moving)
            {
                var line = new RectangleF(pill.X + 6, pill.Bottom - 5, pill.Width - 12, 3);
                Fill(g, line, 1.5f, Color.FromArgb(90, 0, 0, 0));
                if (total > 0)
                {
                    float f = Math.Min(1f, (float)((double)done / total));
                    if (f > 0.01f)
                        Fill(g, new RectangleF(line.X, line.Y, line.Width * f, line.Height), 1.5f, accent);
                }
                else
                {
                    float span = Math.Max(10, line.Width / 3);
                    float travel = line.Width + span;
                    float pos = (Environment.TickCount / 6f) % travel - span;
                    float x1 = Math.Max(line.X, line.X + pos);
                    float x2 = Math.Min(line.Right, line.X + pos + span);
                    if (x2 > x1) Fill(g, new RectangleF(x1, line.Y, x2 - x1, line.Height), 1.5f, accent);
                }
            }

            // Красная точка — что-то сорвалось и ждёт решения.
            if (bad > 0)
            {
                using var brBad = new SolidBrush(Bad);
                g.FillEllipse(brBad, pill.Right - 9, pill.Y - 1, 7, 7);
            }
        }
    }

    /// <summary>
    /// Полоса передач ПОД ПЕРЕПИСКОЙ — то же, что на телефоне.
    ///
    /// Кружок в шапке остаётся: он показывает всё разом и переживает переход
    /// между чатами. Но пока отправка идёт из открытого чата, смотреть на неё
    /// и отменять удобнее прямо здесь, не поднимая глаз к шапке. Полоса знает
    /// только ключ переписки («dm:5», «group:3», «chan:7») и сама появляется и
    /// исчезает: своей высоты у пустой полосы нет.
    /// </summary>
    internal sealed class TransferStrip : Panel
    {
        private readonly Func<string> _key;
        private Transfers.Item[] _items = Array.Empty<Transfers.Item>();
        private readonly System.Windows.Forms.Timer _timer;

        private const int MaxRows = 3;

        internal TransferStrip(Func<string> key)
        {
            _key = key ?? (() => "");
            Dock = DockStyle.Bottom;
            Height = 0;
            Visible = false;
            BackColor = Color.FromArgb(54, 57, 63);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint, true);

            // Опрос, а не события: правки приходят с потоков передач, и
            // каждую порцию пришлось бы переводить на поток окна.
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (s, e) => Sync();
            _timer.Start();
        }

        /// <summary>Перечитывает список и подгоняет высоту.</summary>
        internal void Sync()
        {
            try
            {
                Transfers.Expire();
                var items = Transfers.For(_key());
                _items = items;
                int h = TransferUi.HeightFor(Math.Min(items.Length, MaxRows));
                if (Height != h) Height = h;
                bool show = h > 0;
                if (Visible != show) Visible = show;
                if (show) Invalidate();
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var br = new SolidBrush(BackColor)) e.Graphics.FillRectangle(br, ClientRectangle);
            TransferUi.PaintRows(e.Graphics, ClientSize.Width, _items, MaxRows);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left &&
                TransferUi.Click(e.Location, ClientSize.Width, _items, MaxRows))
                Sync();
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = Cursors.Hand;
            base.OnMouseMove(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _timer.Stop(); _timer.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
