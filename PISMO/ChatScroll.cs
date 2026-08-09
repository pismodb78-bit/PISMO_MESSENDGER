using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл насовсем и заменяет
    /// нативный вертикальный на тонкий скруглённый ползунок (как в Discord).
    /// Колесо/прокрутка работают как обычно; ползунок можно тащить мышью.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0, SB_VERT = 1, SB_BOTH = 3;

        private static readonly System.Collections.Generic.HashSet<Panel> _done = new();

        public static void Attach(Panel p)
        {
            if (p == null || _done.Contains(p)) return;
            _done.Add(p);

            // Прячем обе нативные полосы (горизонтальную — навсегда, вертикальную —
            // рисуем свою). AutoScroll и колесо продолжают работать.
            void HideNative()
            {
                try
                {
                    if (p.IsHandleCreated)
                    {
                        ShowScrollBar(p.Handle, SB_HORZ, false);
                        ShowScrollBar(p.Handle, SB_VERT, false);
                    }
                }
                catch { }
            }
            p.HorizontalScroll.Enabled = false;

            var bar = new SlimBar(p) { Width = 8 };
            var host = p.Parent ?? (Control)p;
            host.Controls.Add(bar);
            bar.BringToFront();

            void Reflow()
            {
                HideNative();
                // располагаем полосу у правого края панели
                bar.Location = new Point(p.Right - bar.Width - 2, p.Top + 2);
                bar.Height = p.Height - 4;
                bar.BringToFront();
                bar.Sync();
            }

            p.Layout += (s, e) => Reflow();
            p.Resize += (s, e) => Reflow();
            p.Scroll += (s, e) => { HideNative(); bar.Sync(); };
            p.MouseWheel += (s, e) => { HideNative(); bar.Sync(); };
            p.ControlAdded += (s, e) => { HideNative(); bar.Sync(); };
            host.Resize += (s, e) => Reflow();
            Reflow();
        }

        /// <summary>Тонкий кастомный вертикальный ползунок поверх панели.</summary>
        private sealed class SlimBar : Control
        {
            private readonly Panel _p;
            private bool _hover, _dragging;
            private int _dragOffset;

            public SlimBar(Panel p)
            {
                _p = p;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Default;
                TabStop = false;
            }

            private int Content => Math.Max(_p.DisplayRectangle.Height, _p.ClientSize.Height);
            private int Viewport => _p.ClientSize.Height;
            private int Offset => -_p.AutoScrollPosition.Y;
            private bool Needed => Content > Viewport + 1;

            private (int y, int h) Thumb()
            {
                int track = Height;
                if (!Needed) return (0, 0);
                int th = Math.Max(30, (int)((long)Viewport * track / Content));
                th = Math.Min(th, track);
                int max = Content - Viewport;
                int ty = max <= 0 ? 0 : (int)((long)Offset * (track - th) / max);
                return (ty, th);
            }

            public void Sync()
            {
                Visible = Needed;
                if (Visible) Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (!Needed) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int w = Width - 2;
                var rect = new Rectangle(1, ty, w, th);
                int alpha = _dragging ? 200 : (_hover ? 170 : 120);
                using var br = new SolidBrush(Color.FromArgb(alpha, 130, 134, 145));
                using var path = Rounded(rect, w / 2);
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
                else { ScrollToThumbTop(e.Y - th / 2); }  // клик по треку — прыжок
                Invalidate();
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (_dragging) ScrollToThumbTop(e.Y - _dragOffset);
                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; Invalidate(); base.OnMouseUp(e); }

            private void ScrollToThumbTop(int thumbTop)
            {
                var (_, th) = Thumb();
                int track = Height;
                int max = Content - Viewport;
                if (max <= 0 || track - th <= 0) return;
                thumbTop = Math.Max(0, Math.Min(track - th, thumbTop));
                int off = (int)((long)thumbTop * max / (track - th));
                try { _p.AutoScrollPosition = new Point(0, off); } catch { }
                Invalidate();
            }
        }
    }
}
