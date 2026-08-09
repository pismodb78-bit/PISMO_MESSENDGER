using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл и вешает тонкий
    /// вертикальный ползунок-оверлей.
    ///
    /// Против мерцания: нативную вертикальную полосу НЕ прячем через ShowScrollBar
    /// (движок AutoScroll возвращает её на каждом layout — это и давало мерцание).
    /// Вместо этого оверлей делаем ШИРИНОЙ с нативную полосу и просто ПЕРЕКРЫВАЕМ
    /// её сверху непрозрачным фоном.
    ///
    /// Против «вылезания за чат»: оверлей — сиблинг панели в ЕЁ ЖЕ родителе,
    /// позиция берётся из <see cref="Control.Bounds"/> панели (общий координатный
    /// простор, без PointToScreen), а сами Bounds клампятся в клиентскую область
    /// родителя.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int SB_HORZ = 0;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_COMPOSITED = 0x02000000;

        /// <summary>
        /// Включает WS_EX_COMPOSITED — двойную буферизацию ВМЕСТЕ С дочерними
        /// контролами (сообщениями). Без этого при быстром/плавном скролле
        /// пузыри сообщений «мажут» и двоятся: DoubleBuffered у самой панели
        /// буферизует только её фон, но не дочерние окна.
        /// </summary>
        public static void EnableComposited(Control c)
        {
            void Apply()
            {
                try
                {
                    if (!c.IsHandleCreated) return;
                    int ex = GetWindowLong(c.Handle, GWL_EXSTYLE);
                    SetWindowLong(c.Handle, GWL_EXSTYLE, ex | WS_EX_COMPOSITED);
                }
                catch { }
            }
            c.HandleCreated += (s, e) => Apply();
            Apply();
        }

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

        /// <summary>
        /// Включает двойную буферизацию у панели (у Panel это protected-свойство) —
        /// без неё при медленном скролле AutoScroll-панель «мажет»: сообщения двоятся
        /// и оставляют артефакты.
        /// </summary>
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

        /// <summary>Убирает горизонтальный скролл и вешает тонкий вертикальный ползунок.</summary>
        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);
            EnableDoubleBuffer(p);
            EnableComposited(p);   // главный фикс смаза: буферизация вместе с дочерними
            // Перерисовка ВМЕСТЕ С дочерними (true) при скролле убирает «хвосты» сообщений.
            p.Scroll += (s, e) => p.Invalidate(true);

            if (p.Parent == null) { p.ParentChanged += (s, e) => Attach2(p); return; }
            Attach2(p);
        }

        private static readonly System.Collections.Generic.HashSet<Panel> _done = new();
        private static void Attach2(Panel p)
        {
            var host = p.Parent;
            if (host == null || _done.Contains(p)) return;
            _done.Add(p);

            int bw = SystemInformation.VerticalScrollBarWidth;   // перекрыть нативную полосу
            var bar = new SlimBar(p) { Width = bw };
            host.Controls.Add(bar);
            bar.BringToFront();

            void Reposition()
            {
                try
                {
                    if (p.IsDisposed || bar.IsDisposed) return;
                    int x = p.Right - bw;
                    int y = p.Top;
                    int h = p.Height;
                    // кламп в клиентскую область родителя — чтобы ничто не вылезло
                    if (host is Control hc)
                    {
                        int maxR = hc.ClientSize.Width, maxB = hc.ClientSize.Height;
                        if (x + bw > maxR) x = maxR - bw;
                        if (x < 0) x = 0;
                        if (y < 0) y = 0;
                        if (y + h > maxB) h = Math.Max(0, maxB - y);
                    }
                    bar.Bounds = new Rectangle(x, y, bw, h);
                    bar.Visible = p.Visible && bar.NeedBar;
                    bar.BringToFront();
                    bar.Sync();
                }
                catch { }
            }

            p.Resize += (s, e) => Reposition();
            p.Move += (s, e) => Reposition();
            p.VisibleChanged += (s, e) => Reposition();
            // Layout — ключевой момент: docked-панель получает правильные Top/Height
            // ТОЛЬКО после раскладки. Без этого оверлей вставал по устаревшим
            // координатам (нативная белая полоса светилась до первого скролла, а в
            // ЛС оверлей заезжал в шапку и перекрывал кнопку звонка).
            p.Layout += (s, e) => Reposition();
            host.Layout += (s, e) => Reposition();
            p.Scroll += (s, e) => { bar.BringToFront(); bar.Sync(); };
            p.MouseWheel += (s, e) => { bar.BringToFront(); bar.Sync(); };
            p.ControlAdded += (s, e) => Reposition();
            p.HandleCreated += (s, e) => Reposition();
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
                e.Graphics.Clear(_p.BackColor);   // перекрываем нативную полосу
                if (!NeedBar) return;
                var (ty, th) = Thumb();
                if (th <= 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Тонкий Discord-подобный ползунок: узкий в покое, чуть толще при
                // наведении/перетаскивании. Прижат к правому краю с небольшим зазором.
                int tw = _dragging ? 8 : (_hover ? 7 : 5);
                int gap = 3;                       // зазор от края
                int x = Width - tw - gap;
                var rect = new Rectangle(x, ty + 3, tw, Math.Max(12, th - 6));

                // Цвет под тему: в тёмной — светло-серый, в светлой — тёмно-серый.
                bool light = false;
                try { light = Theme.IsLight; } catch { }
                Color c = light ? Color.FromArgb(136, 140, 150) : Color.FromArgb(180, 184, 194);
                int alpha = _dragging ? 235 : (_hover ? 200 : 130);
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
