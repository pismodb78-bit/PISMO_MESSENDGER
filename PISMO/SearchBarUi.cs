using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Единый стиль строки поиска сообщений (мессенджер и серверные каналы):
    /// одинаковые размеры, цвета и ИКОНКИ.
    ///
    /// Иконки рисуются вручную через GDI+, а не эмодзи-шрифтом: эмодзи 📅 и 🔍 в
    /// маленьких кнопках обрезались (глиф не влезал в кегль), да ещё и выглядели
    /// по-разному в мессенджере и на сервере.
    /// </summary>
    internal static class SearchBarUi
    {
        public enum Icon { Magnifier, Calendar, Up, Down }

        public static readonly Color Fg      = Color.FromArgb(236, 238, 242);   // активный значок
        public static readonly Color FgDim   = Color.FromArgb(130, 133, 140);   // недоступный
        public static readonly Color Back    = Color.FromArgb(47, 49, 54);
        public static readonly Color Hover   = Color.FromArgb(62, 65, 72);
        public static readonly Color BoxBack = Color.FromArgb(30, 31, 34);

        public const int BtnW = 26;   // единый размер кнопок
        public const int BtnH = 24;
        public const int BoxH = 24;   // высота поля ввода
        public const int Gap  = 2;    // зазор между элементами (плотная строка)

        /// <summary>Приводит кнопку к общему стилю и вешает отрисовку значка.</summary>
        public static void Style(Button b, Icon icon)
        {
            if (b == null) return;
            b.Text = "";
            b.Size = new Size(BtnW, BtnH);
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Back;
            b.ForeColor = Fg;                    // цветом значка управляет ForeColor
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Hover;
            b.FlatAppearance.MouseDownBackColor = Hover;
            b.Paint += (s, e) =>
            {
                // Фон закрашиваем сами: у Button со своим Paint иногда оставались
                // артефакты подложки, из-за чего значок выглядел «сломанным».
                using (var bg = new SolidBrush(b.ClientRectangle.Contains(b.PointToClient(Cursor.Position))
                        ? Hover : b.BackColor))
                    e.Graphics.FillRectangle(bg, b.ClientRectangle);
                Draw(e.Graphics, b.ClientRectangle, icon, b.ForeColor);
            };
            b.MouseEnter += (s, e) => b.Invalidate();
            b.MouseLeave += (s, e) => b.Invalidate();
            // Приглушение/подсветку делаем сменой ForeColor — значок должен перерисоваться.
            b.ForeColorChanged += (s, e) => b.Invalidate();
        }

        /// <summary>Приводит поле ввода поиска к общему стилю.</summary>
        public static void StyleBox(TextBox t, string placeholder)
        {
            if (t == null) return;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = BoxBack;
            t.ForeColor = Color.White;
            t.Font = new Font("Segoe UI", 9.5f);
            t.PlaceholderText = placeholder;
            t.Height = BoxH;
        }

        /// <summary>Приводит счётчик совпадений к общему стилю.</summary>
        public static void StyleCount(Label l)
        {
            if (l == null) return;
            l.AutoSize = false;
            l.Size = new Size(36, 20);
            l.ForeColor = Color.FromArgb(150, 152, 158);
            l.Font = new Font("Segoe UI", 8f);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.BackColor = Color.Transparent;
        }

        private static void Draw(Graphics g, Rectangle r, Icon icon, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (icon)
            {
                case Icon.Magnifier: DrawMagnifier(g, r, color); break;
                case Icon.Calendar:  DrawCalendar(g, r, color);  break;
                case Icon.Up:        DrawArrow(g, r, color, true);  break;
                case Icon.Down:      DrawArrow(g, r, color, false); break;
            }
        }

        private static void DrawMagnifier(Graphics g, Rectangle r, Color c)
        {
            using var pen = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int d = Math.Min(r.Width, r.Height) - 12;      // диаметр линзы
            if (d < 6) d = 6;
            int x = r.X + (r.Width - d) / 2 - 1;
            int y = r.Y + (r.Height - d) / 2 - 1;
            g.DrawEllipse(pen, x, y, d, d);
            g.DrawLine(pen, x + d - 1, y + d - 1, x + d + 4, y + d + 4);   // ручка
        }

        private static void DrawCalendar(Graphics g, Rectangle r, Color c)
        {
            int w = Math.Min(r.Width - 9, 15), h = Math.Min(r.Height - 9, 14);
            if (w < 8) w = 8; if (h < 8) h = 8;
            int x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) / 2 + 1;

            using var pen = new Pen(c, 1.4f);
            using var br = new SolidBrush(c);

            // корпус
            g.DrawRectangle(pen, x, y, w, h);
            // верхняя планка
            g.FillRectangle(br, x + 1, y + 1, w - 1, 3);
            // «кольца» сверху
            g.DrawLine(pen, x + 4, y - 3, x + 4, y);
            g.DrawLine(pen, x + w - 4, y - 3, x + w - 4, y);
            // точки-дни
            int dot = 2, gapX = (w - 2) / 3;
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 3; col++)
                {
                    int dx = x + 2 + col * gapX;
                    int dy = y + 7 + row * 4;
                    if (dx + dot <= x + w - 1 && dy + dot <= y + h - 1)
                        g.FillRectangle(br, dx, dy, dot, dot);
                }
        }

        private static void DrawArrow(Graphics g, Rectangle r, Color c, bool up)
        {
            // Симметричные целочисленные координаты: при нечётной высоте одна из
            // стрелок получалась «размазанной» сглаживанием и выглядела бледнее.
            const int half = 6;   // половина ширины основания
            const int rise = 7;   // высота треугольника
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            int top = cy - rise / 2, bottom = cy + rise / 2;
            using var br = new SolidBrush(c);
            Point[] pts = up
                ? new[] { new Point(cx - half, bottom), new Point(cx + half, bottom), new Point(cx, top) }
                : new[] { new Point(cx - half, top), new Point(cx + half, top), new Point(cx, bottom) };
            g.FillPolygon(br, pts);
        }
    }
}
