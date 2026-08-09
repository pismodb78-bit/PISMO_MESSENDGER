using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл и (для чатов) заменяет
    /// нативный ВЕРТИКАЛЬНЫЙ на тонкий скруглённый ползунок (как в Discord).
    ///
    /// Ключевые решения против прошлых багов:
    ///  • нативную вертикаль НЕ прячем — она резервирует место справа, поэтому
    ///    сообщения НЕ лезут под ползунок;
    ///  • наш ползунок НЕпрозрачный (перекрывает нативную полосу) — не мерцает;
    ///  • виден только когда прокрутка реально нужна;
    ///  • тяжёлое (репозиция/BringToFront) — только на ресайз, на скролл лишь
    ///    перерисовка — без мерцания.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _pretty = new();

        /// <summary>Только убрать горизонтальный скролл (сайдбары/списки).</summary>
        public static void KillHorizontal(Panel p)
        {
            if (p == null || _hkill.Contains(p)) return;
            _hkill.Add(p);
            p.HorizontalScroll.Enabled = false;
            void Hide() { try { if (p.IsHandleCreated) ShowScrollBar(p.Handle, SB_HORZ, false); } catch { } }
            p.Layout += (s, e) => Hide();
            p.Resize += (s, e) => Hide();
            p.Scroll += (s, e) => Hide();
            p.ControlAdded += (s, e) => Hide();
            Hide();
        }

        /// <summary>Гор.скролл убрать + красивый тонкий вертикальный ползунок (чаты).</summary>
        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);

            int bw = Math.Max(14, SystemInformation.VerticalScrollBarWidth);
            var bar = new SlimBar(p) { Width = bw, BackColor = p.BackColor };
            var host = p.Parent ?? (Control)p;
            host.Controls.Add(bar);

            void Reposition()
            {
                bar.BackColor = p.BackColor;
                // Ровно над нативной вертикальной полосой (правые bw px панели).
                bar.Location = new Point(p.Right - bw, p.Top);
                bar.Height = p.Height;
                bar.BringToFront();
                bar.Sync();
            }
            p.Resize += (s, e) => Reposition();
            host.Resize += (s, e) => Reposition();
            // Скролл/колесо/добавление сообщений — только перерисовка ползунка.
            p.Scroll += (s, e) => bar.Sync();
            p.MouseWheel += (s, e) => bar.Sync();
            p.ControlAdded += (s, e) => bar.Sync();
            Reposition();
        }

        private sealed class SlimBar : Control
        {
            private readonly Panel _p;
            private bool _hover, _dragging;
            private int _dragOffset;

            public SlimBar(Panel p)
            {
                _p = p;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint, true);
                TabStop = false;
            }

            private int Content => Math.Max(_p.DisplayRectangle.Height, _p.ClientSize.Height);
            private int Viewport => _p.ClientSize.Height;
            private int Offset => -_p.AutoScrollPosition.Y;
            private bool Needed => Content > Viewport + 1;

            private (int y, int h) Thumb()
            {
                int track = Height;
                if (!Needed || track <= 0) return (0, 0);
                int th = Math.Max(28, (int)((long)Viewport * track / Content));
                th = Math.Min(th, track);
                int max = Content - Viewport;
                int ty = max <= 0 ? 0 : (int)((long)Offset * (track - th) / max);
                return (ty, th);
            }

            public void Sync()
            {
                bool need = Needed;
                if (Visible != need) Visible = need;
                if (need) Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(_p.BackColor);   // непрозрачно — перекрываем нативную полосу
                if (!Needed) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int tw = 7;
                int x = (Width - tw) / 2;
                var rect = new Rectangle(x, ty + 1, tw, Math.Max(8, th - 2));
                int alpha = _dragging ? 210 : (_hover ? 185 : 130);
                using var br = new SolidBrush(Color.FromArgb(alpha, 132, 136, 146));
                using var path = Rounded(rect, tw / 2);
                e.Graphics.FillPath(br, path);
            }

            private static GraphicsPath Rounded(Rectangle r, int rad)
            {
                int d = Math.Max(1, rad * 2);
                var p = new GraphicsPath();
                if (d >= r.Width && d >= r.Height) { p.AddEllipse(r); return p; }
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; if (!_dragging) Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                var (ty, th) = Thumb();
                if (e.Y >= ty && e.Y <= ty + th) { _dragging = true; _dragOffset = e.Y - ty; }
                else ScrollToThumbTop(e.Y - th / 2);
                Invalidate();
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_dragging) ScrollToThumbTop(e.Y - _dragOffset);
                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                try { _p.AutoScrollPosition = new Point(0, Offset - Math.Sign(e.Delta) * 60); } catch { }
                Sync();
            }

            private void ScrollToThumbTop(int thumbTop)
            {
                var (_, th) = Thumb();
                int track = Height, max = Content - Viewport;
                if (max <= 0 || track - th <= 0) return;
                thumbTop = Math.Max(0, Math.Min(track - th, thumbTop));
                int off = (int)((long)thumbTop * max / (track - th));
                try { _p.AutoScrollPosition = new Point(0, off); } catch { }
                Invalidate();
            }
        }
    }
}
