using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Прокрутка чатов/списков: убираем ГОРИЗОНТАЛЬНУЮ нативную полосу, а
    /// ВЕРТИКАЛЬНУЮ оставляем нативной, но переводим её в ТЁМНЫЙ стиль Windows
    /// (SetWindowTheme "DarkMode_Explorer" + тёмный режим процесса). Кастомный
    /// ползунок убран по просьбе — только тёмная нативная полоса.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_STYLE = -16;
        private const int WS_CLIPCHILDREN = 0x02000000;
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string sub, string ids);
        // Недокументированные ordinals uxtheme — обязательны, чтобы тёмная полоса реально применилась.
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int mode);                       // #135 (1903+)
        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);      // #133
        [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)]
        private static extern void FlushMenuThemes();                                    // #136

        private const int SB_HORZ = 0;
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_THEMECHANGED = 0x031A;
        private const int APPMODE_FORCE_DARK = 2;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static bool _appDark;

        /// <summary>Разово включает тёмный режим для всего процесса (Win10 1903+) — без него
        /// тёмный стиль полос подхватывается не на всех окнах.</summary>
        public static void EnableAppDarkMode()
        {
            if (_appDark) return;
            _appDark = true;
            try { SetPreferredAppMode(APPMODE_FORCE_DARK); FlushMenuThemes(); } catch { }
        }

        /// <summary>Переводит нативную полосу контрола в тёмный стиль. Полный рабочий рецепт:
        /// AllowDarkModeForWindow → SetWindowTheme(DarkMode_Explorer) → WM_THEMECHANGED.
        /// Без AllowDarkModeForWindow и WM_THEMECHANGED тёмная полоса НЕ применяется.</summary>
        public static void ApplyDarkScrollbar(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                bool light = false;
                try { light = Theme.IsLight; } catch { }
                bool dark = !light;
                EnableAppDarkMode();
                try { AllowDarkModeForWindow(c.Handle, dark); } catch { }
                SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                try { SendMessage(c.Handle, WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
            catch { }
        }

        /// <summary>Прячет горизонтальную нативную полосу (в т.ч. в оконном режиме).</summary>
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

        /// <summary>Убирает горизонтальный скролл, делает вертикальную нативную полосу
        /// тёмной и включает плавную прокрутку колесом.
        /// <paramref name="onScrolled"/> — вызывается на кадрах анимации: колесо мы
        /// перехватываем до панели, поэтому её событие MouseWheel больше не срабатывает,
        /// а логика догрузки старых сообщений и кнопки «вниз» на нём висела.</summary>
        public static void Attach(Panel p, Action onScrolled = null)
        {
            if (p == null) return;
            KillHorizontal(p);
            EnableAppDarkMode();
            void Apply() => ApplyDarkScrollbar(p);
            p.HandleCreated += (s, e) => Apply();
            p.Resize += (s, e) => Apply();
            p.VisibleChanged += (s, e) => Apply();
            Apply();

            EnableDoubleBuffer(p);

            // Артефакты при быстрой прокрутке = отставание перерисовки: Invalidate лишь
            // ставит WM_PAINT в очередь, и следующий шаг прокрутки приходит раньше, чем
            // очередь разгребётся («не успевает отрисоваться»). RedrawWindow с
            // UPDATENOW|ALLCHILDREN перерисовывает панель И все пузыри СИНХРОННО.
            // ВАЖНО: прокрутка КОЛЕСОМ у Panel не поднимает событие Scroll — поэтому
            // одного обработчика Scroll мало, при быстром прокручивании колесом
            // артефакты оставались. Перехватываем сообщения окна (WM_VSCROLL и
            // WM_MOUSEWHEEL) и перерисовываем синхронно после их обработки — это
            // покрывает ВСЕ способы прокрутки: колесо, перетаскивание полосы,
            // клавиатуру.
            var scroller = new SmoothScroller(p, onScrolled);
            var rp = new ScrollRepainter(p, scroller);
            void HookRepaint()
            {
                try { if (p.IsHandleCreated && rp.Handle == IntPtr.Zero) rp.AssignHandle(p.Handle); } catch { }
            }
            p.HandleCreated += (s, e) => HookRepaint();
            p.HandleDestroyed += (s, e) => { try { rp.ReleaseHandle(); } catch { } };
            HookRepaint();
        }

        /// <summary>
        /// Плавная прокрутка колесом: ведём позицию к цели по кадрам с замедлением.
        ///
        /// Против дёрганья (было на сервере): лента там периодически перерисовывается и
        /// САМА восстанавливает позицию прокрутки, а также срабатывает догрузка старых
        /// сообщений. Наша анимация с этим воевала — позицию тянуло туда-сюда. Теперь
        /// запоминаем, какое значение выставили сами; если на следующем кадре позиция
        /// оказалась другой (её сдвинул кто-то ещё) — анимацию прекращаем, а не боремся.
        /// </summary>
        private sealed class SmoothScroller
        {
            private const int StepPx = 110;      // прокрутка за один щелчок колеса
            private const double Ease = 0.16;    // доля оставшегося пути за кадр
            private const int MinFrameMs = 16;   // ~60 кадров/с

            private readonly Panel _p;
            private readonly System.Windows.Forms.Timer _t;
            private readonly Action _onScrolled;
            private int _target;
            private int _lastSet = int.MinValue;   // что выставили мы сами

            public SmoothScroller(Panel p, Action onScrolled)
            {
                _p = p;
                _onScrolled = onScrolled;
                _t = new System.Windows.Forms.Timer { Interval = MinFrameMs };
                _t.Tick += Tick;
                p.Disposed += (s, e) => { try { _t.Stop(); _t.Dispose(); } catch { } };
            }

            private int MaxOffset => Math.Max(0, _p.DisplayRectangle.Height - _p.ClientSize.Height);
            private int Current => -_p.AutoScrollPosition.Y;
            private int Clamp(int v) => Math.Max(0, Math.Min(MaxOffset, v));

            /// <summary>Щелчок колеса. true — сообщение обработано, панели его не отдаём.
            /// Щелчки НАКАПЛИВАЮТСЯ: в ту же сторону — цель дальше (прокрутка разгоняется),
            /// в обратную — цель возвращается назад (сначала гасит ход, затем идёт обратно).</summary>
            public bool TryHandleWheel(int delta)
            {
                if (MaxOffset <= 0) return false;   // прокручивать нечего — пусть решает панель

                if (!_t.Enabled) _target = Current;              // старт с текущей позиции
                _target = Clamp(_target - Math.Sign(delta) * StepPx);   // накопление
                if (!_t.Enabled) { _lastSet = Current; _t.Start(); }
                return true;
            }

            private void Tick(object sender, EventArgs e)
            {
                if (_p.IsDisposed || !_p.IsHandleCreated) { _t.Stop(); return; }

                int cur = Current;
                // Позицию сдвинул кто-то другой (перерисовка ленты / догрузка / ползунок)
                // — прекращаем анимацию, чтобы не дёргать взад-вперёд.
                if (_lastSet != int.MinValue && Math.Abs(cur - _lastSet) > 2) { _t.Stop(); return; }

                _target = Clamp(_target);
                int diff = _target - cur;
                if (Math.Abs(diff) <= 1)
                {
                    try { _p.AutoScrollPosition = new Point(0, _target); } catch { }
                    RepaintNow(_p, force: true);   // финальный кадр — обязательно
                    _t.Stop();
                    try { _onScrolled?.Invoke(); } catch { }
                    return;
                }

                int step = (int)Math.Round(diff * Ease);
                if (step == 0) step = Math.Sign(diff);
                try { _p.AutoScrollPosition = new Point(0, cur + step); } catch { }
                // Кадр анимации двигает позицию напрямую и НЕ порождает WM_VSCROLL/
                // WM_MOUSEWHEEL, поэтому перерисовываем сами (с ограничением частоты).
                RepaintNow(_p);
                _lastSet = Current;   // фактическое (могло быть подрезано)
                try { _onScrolled?.Invoke(); } catch { }   // пагинация и кнопка «вниз»
            }
        }

        private sealed class ScrollRepainter : NativeWindow
        {
            private const int WM_VSCROLL = 0x0115;
            private const int WM_MOUSEWHEEL = 0x020A;
            private readonly Panel _p;
            private readonly SmoothScroller _s;

            public ScrollRepainter(Panel p, SmoothScroller s) { _p = p; _s = s; }

            protected override void WndProc(ref Message m)
            {
                // Колесо перехватываем ДО панели и НЕ отдаём ей: иначе Panel сначала сам
                // прыгает на 3 строки, и только потом срабатывает наш обработчик —
                // получается двойное движение, а защита от чужого сдвига обрывала
                // анимацию (щелчок в ту же сторону «резко останавливал» прокрутку).
                if (m.Msg == WM_MOUSEWHEEL)
                {
                    int delta = (short)((long)m.WParam >> 16);
                    if (_s.TryHandleWheel(delta)) { m.Result = IntPtr.Zero; return; }
                }

                base.WndProc(ref m);
                if (m.Msg == WM_VSCROLL)
                    RepaintNow(_p);   // панель + дети + перекрывающие соседи (кнопка «вниз»)
            }
        }


        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;

        /// <summary>Немедленная (синхронная) перерисовка контрола вместе со всеми дочерними
        /// И с перекрывающими его СОСЕДЯМИ (плавающая кнопка «вниз к новым» — не ребёнок
        /// панели, а её сосед поверх неё, поэтому ALLCHILDREN её не покрывает и в её
        /// области картинка рвалась, как только кнопка появлялась).</summary>
        public static void RepaintNow(Control c) => RepaintNow(c, false);

        // Синхронная перерисовка ленты — дорогая операция (десятки пузырей со своими
        // дочерними контролами). Без ограничения частоты она съедала CPU и приложение
        // «фризило». Ограничиваем: не чаще ~50 раз в секунду; финальный кадр всегда
        // рисуем принудительно (force), чтобы картинка гарантированно устоялась.
        private static int _lastRepaintTick;
        private const int RepaintMinIntervalMs = 20;

        public static void RepaintNow(Control c, bool force)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;

                int now = Environment.TickCount;
                if (!force && unchecked(now - _lastRepaintTick) < RepaintMinIntervalMs) return;
                _lastRepaintTick = now;

                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);

                var parent = c.Parent;
                if (parent == null) return;
                foreach (Control sib in parent.Controls)
                {
                    if (ReferenceEquals(sib, c) || !sib.Visible || !sib.IsHandleCreated) continue;
                    if (!sib.Bounds.IntersectsWith(c.Bounds)) continue;   // только перекрывающие
                    RedrawWindow(sib.Handle, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                }
            }
            catch { }
        }

        /// <summary>Для панелей сообщений: как Attach + двойная буферизация.
        /// WS_CLIPCHILDREN НЕ трогаем — его снятие рвало отрисовку пузырей.</summary>
        public static void AttachChat(Panel p, Action onScrolled = null)
        {
            Attach(p, onScrolled);
            EnableDoubleBuffer(p);
        }

        private static readonly System.Reflection.PropertyInfo _dbProp =
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public static void EnableDoubleBuffer(Control c)
        {
            try { _dbProp?.SetValue(c, true); } catch { }
        }

        /// <summary>Двойная буферизация контрола И ВСЕХ вложенных (аватар/имя/текст/время
        /// пузыря — это отдельные дочерние окна, каждое из которых «рвётся» при скролле,
        /// если его не буферизовать).</summary>
        public static void EnableDoubleBufferDeep(Control c)
        {
            if (c == null) return;
            EnableDoubleBuffer(c);
            foreach (Control child in c.Controls) EnableDoubleBufferDeep(child);
            // Новые дети (добавляются после построения) — тоже буферизуем.
            c.ControlAdded -= _dbOnAdd;
            c.ControlAdded += _dbOnAdd;
        }
        private static readonly ControlEventHandler _dbOnAdd = (s, e) => EnableDoubleBufferDeep(e.Control);

        // Заморозка отрисовки через WM_SETREDRAW убрана: при наложении авто-обновления
        // сервера на прокрутку она оставляла «рваные» полу-отрисованные пузыри.
        // Достаточно SuspendLayout/ResumeLayout в самих методах пересборки.
        public static void SuspendDraw(Control c) { }
        public static void ResumeDraw(Control c) { }
    }
}
