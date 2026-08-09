using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл и (для чатов) рисует тонкий
    /// вертикальный ползунок. Ползунок вешается на ВЕРХНЮЮ форму (всегда поверх
    /// всего) и позиционируется по экранным координатам панели — так он гарантированно
    /// виден (прошлые версии терялись под докнутыми панелями по z-order).
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _pretty = new();

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

        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);

            var form = p.FindForm();
            if (form == null) return;   // без формы ползунок вешать некуда

            int bw = 12;
            var bar = new SlimBar(p) { Width = bw };
            form.Controls.Add(bar);
            bar.BringToFront();

            void Reposition()
            {
                try
                {
                    if (!p.IsHandleCreated || !form.IsHandleCreated) return;
                    // правый край клиентской области панели -> координаты формы
                    var scr = p.PointToScreen(new Point(p.ClientSize.Width - bw, 0));
                    var pt = form.PointToClient(scr);
                    bar.Location = pt;
                    bar.Height = p.ClientSize.Height;
                    bar.BringToFront();
                    bar.Sync();
                }
                catch { }
            }

            p.Resize += (s, e) => Reposition();
            p.Move += (s, e) => Reposition();
            p.VisibleChanged += (s, e) => { bar.Visible = p.Visible && bar.NeedBar; Reposition(); };
            form.Resize += (s, e) => Reposition();
            form.Move += (s, e) => Reposition();
            p.Scroll += (s, e) => { bar.BringToFront(); bar.Sync(); };
            p.MouseWheel += (s, e) => { bar.BringToFront(); bar.Sync(); };
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
                         | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            private int Content => Math.Max(_p.DisplayRectangle.Height, _p.ClientSize.Height);
            private int Viewport => _p.ClientSize.Height;
            private int Offset => -_p.AutoScrollPosition.Y;
            public bool NeedBar => _p.Visible && Content > Viewport + 1;

            private (int y, int h) Thumb()
            {
                int track = Height;
                if (!NeedBar || track <= 0) return (0, 0);
                int th = Math.Max(28, (int)((long)Viewport * track / Content));
                th = Math.Min(th, track);
                int max = Content - Viewport;
                int ty = max <= 0 ? 0 : (int)((long)Offset * (track - th) / max);
                return (ty, th);
            }

            public void Sync()
            {
                bool need = NeedBar;
                if (Visible != need) Visible = need;
                if (need) Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (!NeedBar) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int tw = 7;
                int x = (Width - tw) / 2;
                var rect = new Rectangle(x, ty + 1, tw, Math.Max(8, th - 2));
                int alpha = _dragging ? 220 : (_hover ? 190 : 140);
                using var br = new SolidBrush(Color.FromArgb(alpha, 140, 144, 154));
                using var path = Rounded(rect, tw / 2);
                g.FillPath(br, path);
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
