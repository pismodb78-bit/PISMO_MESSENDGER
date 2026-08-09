using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл и рисует тонкий
    /// «дискордовский» ВЕРТИКАЛЬНЫЙ ползунок.
    ///
    /// Ключевое отличие от прошлых (не работавших) версий: оверлей — это СИБЛИНГ
    /// панели в ЕЁ ЖЕ родителе и позиционируется прямо по <see cref="Control.Bounds"/>
    /// панели, БЕЗ пересчёта экранных координат (PointToScreen/PointToClient), из-за
    /// которого он раньше промахивался и «не появлялся». Нативная вертикальная полоса
    /// прячется (SB_VERT), прокрутка идёт через AutoScrollPosition — колесо, drag,
    /// клик по треку работают.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0;
        private const int SB_VERT = 1;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _pretty = new();

        /// <summary>Прячет горизонтальную полосу прокрутки у панели (в т.ч. в оконном режиме).</summary>
        public static void KillHorizontal(Panel p)
        {
            if (p == null || _hkill.Contains(p)) return;
            _hkill.Add(p);
            p.HorizontalScroll.Enabled = false;
            p.HorizontalScroll.Visible = false;
            void Hide() { try { if (p.IsHandleCreated) ShowScrollBar(p.Handle, SB_HORZ, false); } catch { } }
            p.Layout += (s, e) => Hide();
            p.Resize += (s, e) => Hide();
            p.Scroll += (s, e) => Hide();
            p.ControlAdded += (s, e) => Hide();
            p.HandleCreated += (s, e) => Hide();
            Hide();
        }

        /// <summary>Убирает горизонтальный скролл и вешает тонкий вертикальный ползунок-оверлей.</summary>
        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);

            var host = p.Parent;
            if (host == null) { p.ParentChanged += (s, e) => Attach2(p); return; }
            Attach2(p);
        }

        private static readonly System.Collections.Generic.HashSet<Panel> _done = new();
        private static void Attach2(Panel p)
        {
            var host = p.Parent;
            if (host == null || _done.Contains(p)) return;
            _done.Add(p);

            const int bw = 10;
            var bar = new SlimBar(p) { Width = bw };
            host.Controls.Add(bar);
            bar.BringToFront();

            void HideVert() { try { if (p.IsHandleCreated) ShowScrollBar(p.Handle, SB_VERT, false); } catch { } }

            void Reposition()
            {
                try
                {
                    if (p.IsDisposed || bar.IsDisposed) return;
                    // Тот же координатный простор, что и панель (общий родитель) —
                    // никаких пересчётов экранных координат.
                    bar.Bounds = new Rectangle(p.Right - bw, p.Top, bw, p.Height);
                    bar.BringToFront();
                    bar.Visible = p.Visible && bar.NeedBar;
                    HideVert();
                    bar.Sync();
                }
                catch { }
            }

            p.Resize += (s, e) => Reposition();
            p.Move += (s, e) => Reposition();
            p.VisibleChanged += (s, e) => Reposition();
            p.Layout += (s, e) => Reposition();
            p.Scroll += (s, e) => { HideVert(); bar.BringToFront(); bar.Sync(); };
            p.MouseWheel += (s, e) => { HideVert(); bar.BringToFront(); bar.Sync(); };
            p.ControlAdded += (s, e) => { HideVert(); Reposition(); };
            p.HandleCreated += (s, e) => { HideVert(); Reposition(); };
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
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = p.BackColor;
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
                int th = Math.Max(30, (int)((long)Viewport * track / Content));
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
                // Фон под цвет чата (перекрывает нативную полосу).
                e.Graphics.Clear(_p.BackColor);
                if (!NeedBar) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int tw = _hover || _dragging ? 8 : 6;
                int x = (Width - tw) / 2;
                var rect = new Rectangle(x, ty + 2, tw, Math.Max(10, th - 4));
                int alpha = _dragging ? 230 : (_hover ? 200 : 150);
                using var br = new SolidBrush(Color.FromArgb(alpha, 150, 154, 164));
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
