using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PISMO
{
    /// <summary>
    /// Векторные значки мьюта в стиле Discord: перечёркнутый микрофон (мьют мик-
    /// рофона) или наушники (deafen). Единый рендер для плиток звонка и списка
    /// участников голосового канала. Без эмодзи-шрифтов — чисто и одинаково.
    /// </summary>
    internal static class MuteGlyph
    {
        private static readonly Color Red = Color.FromArgb(237, 66, 69);

        /// <summary>Рисует значок в квадрате box. deaf=true — наушники, иначе микрофон.
        /// slashBg — цвет «выреза» под перечёркивающей линией (фон плитки/чипа).</summary>
        public static void Draw(Graphics g, RectangleF box, bool deaf, Color slashBg)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(box.Width, box.Height);
            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;
            float stroke = Math.Max(1.4f, s * 0.09f);
            using var pen = new Pen(Red, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(Red);

            if (deaf) DrawHeadphones(g, cx, cy, s, pen, fill);
            else DrawMic(g, cx, cy, s, pen, fill);

            // Перечёркивающая линия с «вырезом»: сначала толстая линия цветом фона,
            // затем красная поверх — получается аккуратный зазор, как в Discord.
            float slashPad = s * 0.14f;
            var p1 = new PointF(box.X + slashPad, box.Bottom - slashPad);
            var p2 = new PointF(box.Right - slashPad, box.Y + slashPad);
            using (var bgPen = new Pen(slashBg, stroke * 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(bgPen, p1, p2);
            g.DrawLine(pen, p1, p2);

            g.SmoothingMode = old;
        }

        private static void DrawMic(Graphics g, float cx, float cy, float s, Pen pen, Brush fill)
        {
            // Капсула-корпус.
            float bw = s * 0.26f, bh = s * 0.40f;
            var body = new RectangleF(cx - bw / 2f, cy - s * 0.30f, bw, bh);
            using (var cap = Rounded(body, bw / 2f)) g.FillPath(fill, cap);
            // Дуга-держатель (U снизу).
            float r = s * 0.24f;
            g.DrawArc(pen, cx - r, cy - r * 0.55f, r * 2f, r * 1.6f, 25, 130);
            // Ножка + основание.
            g.DrawLine(pen, cx, cy + s * 0.20f, cx, cy + s * 0.32f);
            g.DrawLine(pen, cx - s * 0.13f, cy + s * 0.32f, cx + s * 0.13f, cy + s * 0.32f);
        }

        private static void DrawHeadphones(Graphics g, float cx, float cy, float s, Pen pen, Brush fill)
        {
            // Оголовье (верхняя дуга).
            float r = s * 0.30f;
            g.DrawArc(pen, cx - r, cy - r * 0.9f, r * 2f, r * 2f, 180, 180);
            // Две чашки — скруглённые прямоугольники на концах дуги.
            float ew = s * 0.14f, eh = s * 0.24f;
            using (var l = Rounded(new RectangleF(cx - r - ew / 2f, cy - r * 0.1f, ew, eh), ew / 2f)) g.FillPath(fill, l);
            using (var rr = Rounded(new RectangleF(cx + r - ew / 2f, cy - r * 0.1f, ew, eh), ew / 2f)) g.FillPath(fill, rr);
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var path = new GraphicsPath();
            float d = rad * 2f;
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
