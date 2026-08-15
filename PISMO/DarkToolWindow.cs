using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Тёмное безрамочное окно со своим заголовком — вместо системного
    /// SizableToolWindow, чей светлый заголовок выбивался из оформления
    /// приложения.
    ///
    /// Содержимое класть в <see cref="Content"/>: это прокручиваемая панель БЕЗ
    /// горизонтальной полосы (ChatScroll.Attach), поэтому длинные подписи не
    /// вылезают за край и не заставляют окно ездить вбок.
    ///
    /// Окно остаётся тянущимся: рамки у безрамочной формы нет, поэтому размер
    /// меняется через ответ на WM_NCHITTEST по краям.
    /// </summary>
    internal class DarkToolWindow : Form
    {
        private const int Grip = 6;        // толщина зоны захвата у краёв
        private const int HeaderH = 36;

        private static readonly Color Back = Color.FromArgb(32, 34, 37);
        private static readonly Color HeaderBack = Color.FromArgb(24, 25, 28);
        private static readonly Color Edge = Color.FromArgb(58, 61, 68);

        /// <summary>Панель для содержимого (прокрутка только вертикальная).</summary>
        public Panel Content { get; }

        public DarkToolWindow(string title, Size clientSize)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Back;
            ClientSize = clientSize;
            MinimumSize = new Size(300, 220);
            KeyPreview = true;

            var header = new Panel { Dock = DockStyle.Top, Height = HeaderH, BackColor = HeaderBack };
            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(232, 234, 238),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };
            var close = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 40,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(190, 192, 196),
                BackColor = HeaderBack,
                Font = new Font("Segoe UI", 10f),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(237, 66, 69);
            close.Click += (s, e) => Close();

            // Тянуть окно за заголовок: отдаём перетаскивание системе, иначе на
            // безрамочной форме окно нечем двигать.
            void StartDrag(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
                catch { }
            }
            header.MouseDown += StartDrag;
            lbl.MouseDown += StartDrag;

            header.Controls.Add(lbl);
            header.Controls.Add(close);

            // Отступы держит ВНЕШНЯЯ панель, а прокручивается внутренняя. Иначе у
            // ScrollableControl с Padding координаты детей отсчитываются от
            // DisplayRectangle, и x=0 означал бы не то, что ожидаешь.
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Back, Padding = new Padding(14, 12, 14, 14) };
            Content = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Back,
                AutoScroll = true
            };
            outer.Controls.Add(Content);

            Controls.Add(outer);
            Controls.Add(header);   // outer добавлен раньше => докается последним и занимает остаток

            // Тёмная полоса и снятие горизонтальной — да, а вот ПЛАВНУЮ прокрутку
            // (ChatScroll.Attach) здесь НЕ включаем. Она анимирует прокрутку по
            // кадрам и рассчитана на ленту чата; в окне настроек во время
            // демонстрации экрана кадры не успевают, и прокрутка становится
            // медленной и рваной. Нативная прокрутка мгновенная.
            try { ChatScroll.KillHorizontal(Content); } catch { }
            try { ChatScroll.EnableDoubleBuffer(Content); } catch { }
            Content.HandleCreated += (s, e) => { try { ChatScroll.ApplyDarkScrollbar(Content); } catch { } };

            // Ширину дочерних контролов держим сами (см. Stretch/PinRight).
            Content.ClientSizeChanged += (s, e) => Reflow();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        // ── Раскладка по ширине ─────────────────────────────────────────
        // Anchor тут не годится: внутри AutoScroll-панели он отсчитывается от
        // DisplayRectangle (ширина СОДЕРЖИМОГО), поэтому при появлении вертикальной
        // полосы контролы не сжимались, а уходили под обрез — пропадали стрелки
        // списков и обрывались подсказки. Ширину пересчитываем вручную от
        // ClientSize, который полосу уже учитывает.
        private readonly System.Collections.Generic.List<Control> _stretch = new();
        private readonly System.Collections.Generic.List<Control> _pinRight = new();

        /// <summary>Контрол тянется во всю ширину панели содержимого.</summary>
        public T Stretch<T>(T c) where T : Control { _stretch.Add(c); return c; }

        /// <summary>Контрол прижат к правому краю (ширина сохраняется).</summary>
        public T PinRight<T>(T c) where T : Control { _pinRight.Add(c); return c; }

        /// <summary>Доступная ширина содержимого (без вертикальной полосы).</summary>
        public int ContentWidth => Math.Max(120, Content.ClientSize.Width);

        /// <summary>Пересчитать ширину/положение зарегистрированных контролов.</summary>
        public void Reflow()
        {
            int w = Content.ClientSize.Width;
            if (w <= 0) return;
            foreach (var c in _stretch)
            {
                if (c == null || c.IsDisposed) continue;
                if (c.Left != 0) c.Left = 0;
                if (c.Width != w) c.Width = w;
            }
            foreach (var c in _pinRight)
            {
                if (c == null || c.IsDisposed) continue;
                int x = w - c.Width;
                if (c.Left != x) c.Left = x;
            }
        }

        /// <summary>Рамка в 1px: без неё тёмное безрамочное окно сливается с тёмным фоном.</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Edge, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

        /// <summary>Не активировать окно при показе — оно вспомогательное.</summary>
        protected override bool ShowWithoutActivation => false;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                // Растягивание за края: у безрамочного окна системных рамок нет,
                // поэтому границы обозначаем сами.
                if ((int)m.Result == HTCLIENT)
                {
                    // ToInt64, а не ToInt32: на 64-битной системе координаты
                    // упакованы в младшие 32 бита, и при отрицательном Y (окно выше
                    // верха экрана, второй монитор) ToInt32 бросает OverflowException
                    // прямо из WndProc.
                    long lp = m.LParam.ToInt64();
                    var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    bool l = pt.X <= Grip, r = pt.X >= ClientSize.Width - Grip;
                    bool t = pt.Y <= Grip, b = pt.Y >= ClientSize.Height - Grip;
                    int hit =
                        l && t ? HTTOPLEFT : r && t ? HTTOPRIGHT :
                        l && b ? HTBOTTOMLEFT : r && b ? HTBOTTOMRIGHT :
                        l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT;
                    m.Result = (IntPtr)hit;
                }
                return;
            }
            base.WndProc(ref m);
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCLIENT = 1, HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
