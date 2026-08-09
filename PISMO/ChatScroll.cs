using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Прокрутка чатов/списков: прячем ОБЕ нативные полосы (ShowScrollBar — тот же
    /// приём, что надёжно убирал горизонтальную) и рисуем СВОЙ тонкий вертикальный
    /// ползунок. Трек (дорожка) закрашивается цветом фона панели — поэтому граница
    /// полосы полностью сливается с приложением; виден только серый бегунок.
    ///
    /// Ползунок — сиблинг панели в её родителе, позиция берётся из Bounds панели и
    /// зажимается в её прямоугольник (не лезет в шапку / на кнопку звонка). Общий
    /// таймер (120мс) держит его на месте и видимым независимо от того, какие
    /// события раскладки сработали.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
        private const int SB_HORZ = 0;
        private const int SB_VERT = 1;
        private const int SB_BOTH = 3;
        private const int WM_SETREDRAW = 0x000B;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _pretty = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _done = new();

        private static readonly System.Collections.Generic.List<Action> _tickers = new();
        private static System.Windows.Forms.Timer _timer;
        private static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = 120 };
            _timer.Tick += (s, e) => { for (int i = 0; i < _tickers.Count; i++) try { _tickers[i](); } catch { } };
            _timer.Start();
        }

        /// <summary>Прячет горизонтальную нативную полосу (и вертикальную — если на панели рисуем свой ползунок).</summary>
        public static void KillHorizontal(Panel p)
        {
            if (p == null || _hkill.Contains(p)) return;
            _hkill.Add(p);
            p.HorizontalScroll.Enabled = false;
            p.HorizontalScroll.Visible = false;
            void Hide()
            {
                try
                {
                    if (!p.IsHandleCreated) return;
                    ShowScrollBar(p.Handle, _pretty.Contains(p) ? SB_BOTH : SB_HORZ, false);
                }
                catch { }
            }
            p.Layout += (s, e) => Hide();
            p.Resize += (s, e) => Hide();
            p.Scroll += (s, e) => Hide();
            p.ControlAdded += (s, e) => Hide();
            p.HandleCreated += (s, e) => Hide();
            p.Paint += (s, e) => Hide();
            Hide();
        }

        public static void EnableDoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(c, true);
            }
            catch { }
        }

        /// <summary>Прячет нативные полосы и вешает тонкий вертикальный ползунок со сливающимся треком.</summary>
        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);
            EnableDoubleBuffer(p);
            p.Scroll += (s, e) => p.Invalidate(true);
            p.MouseWheel += (s, e) => p.Invalidate(true);

            if (p.Parent == null) { p.ParentChanged += (s, e) => Attach2(p); return; }
            Attach2(p);
        }

        public static void AttachChat(Panel p) => Attach(p);

        private static void Attach2(Panel p)
        {
            var host = p.Parent;
            if (host == null || _done.Contains(p)) return;
            _done.Add(p);

            int bw = 12;   // ширина зоны ползунка (трек невидим — цвет фона)
            var bar = new SlimBar(p) { Width = bw, Visible = false };
            host.Controls.Add(bar);
            bar.BringToFront();

            int lastOffset = int.MinValue;
            void Reposition()
            {
                try
                {
                    if (p.IsDisposed || bar.IsDisposed) return;
                    if (!p.Visible || p.Width < 20 || p.Height < 20) { if (bar.Visible) bar.Visible = false; return; }
                    int x = p.Right - bw, y = p.Top, h = p.Height;
                    if (host is Control hc)
                    {
                        int maxR = hc.ClientSize.Width, maxB = hc.ClientSize.Height;
                        if (x + bw > maxR) x = maxR - bw;
                        if (x < 0) x = 0;
                        if (y < 0) y = 0;
                        if (y + h > maxB) h = Math.Max(0, maxB - y);
                    }
                    var want = new Rectangle(x, y, bw, h);
                    if (bar.Bounds != want) bar.Bounds = want;

                    bool need = bar.NeedBar;
                    if (bar.Visible != need) bar.Visible = need;
                    if (!need) return;

                    if (host.Controls.GetChildIndex(bar) != 0) bar.BringToFront();
                    int off = -p.AutoScrollPosition.Y;
                    if (off != lastOffset) { lastOffset = off; bar.Invalidate(); }
                }
                catch { }
            }

            p.Resize += (s, e) => Reposition();
            p.Move += (s, e) => Reposition();
            p.VisibleChanged += (s, e) => Reposition();
            p.Layout += (s, e) => Reposition();
            host.Layout += (s, e) => Reposition();
            _tickers.Add(Reposition);
            EnsureTimer();
            Reposition();
            try { p.BeginInvoke(new Action(Reposition)); } catch { }
        }

        public static void ApplyDarkScrollbar(Control c) { /* больше не нужно: рисуем свой ползунок */ }

        public static void EnableAppDarkMode() { /* нативные полосы скрыты — тёмный режим не требуется */ }

        public static void SuspendDraw(Control c)
        {
            try { if (c != null && c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, false, 0); } catch { }
        }

        public static void ResumeDraw(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                SendMessage(c.Handle, WM_SETREDRAW, true, 0);
                c.Invalidate(true);
            }
            catch { }
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
                // Трек = цвет фона панели → граница полосы сливается с приложением.
                e.Graphics.Clear(_p.BackColor);
                if (!NeedBar) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int tw = _dragging ? 8 : (_hover ? 7 : 5);
                int gap = 2;
                int x = Width - tw - gap;
                var rect = new Rectangle(x, ty + 3, tw, Math.Max(12, th - 6));
                bool light = false;
                try { light = Theme.IsLight; } catch { }
                Color c = light ? Color.FromArgb(150, 154, 164) : Color.FromArgb(120, 124, 134);
                int alpha = _dragging ? 235 : (_hover ? 205 : 150);
                using var br = new SolidBrush(Color.FromArgb(alpha, c));
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
