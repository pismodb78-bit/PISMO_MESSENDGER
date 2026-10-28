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
        private static readonly System.Collections.Generic.HashSet<Panel> _attached = new();
        private static readonly System.Collections.Generic.Dictionary<Panel, SmoothScroller> _scrollers = new();

        /// <summary>
        /// Содержимое над видимой частью выросло на <paramref name="dy"/> и
        /// позицию прокрутки сдвинули на столько же (догрузка истории сверху).
        /// Идущую прокрутку колесом сдвигаем вместе с ней: иначе плавный
        /// скроллер принял бы сдвиг за чужое вмешательство и встал посреди
        /// движения — прокрутка вверх обрывалась бы на каждой догрузке.
        /// </summary>
        public static void Cancel(Panel p)
        {
            if (p != null && _scrollers.TryGetValue(p, out var scroller)) scroller.Cancel();
        }

        public static void ShiftBy(Panel p, int dy)
        {
            if (p == null || dy == 0) return;
            if (_scrollers.TryGetValue(p, out var s)) s.ShiftBy(dy);
        }
        private static bool _appDark;

        // Панели, у которых прямо сейчас идёт плавная прокрутка. Пока она идёт, окна,
        // перекрывающие панель (кнопка «вниз»), прячем: перекрывающее окно не даёт
        // Windows сдвинуть содержимое быстрым путём (ScrollWindowEx) и заставляет
        // перерисовывать область под собой на каждом шаге — отсюда рывки и фризы.
        private static readonly System.Collections.Generic.HashSet<Panel> _animating = new();

        /// <summary>Идёт ли сейчас плавная прокрутка этой панели.</summary>
        public static bool IsScrolling(Panel p) => p != null && _animating.Contains(p);

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
            // Защита от повторного подключения: Attach вызывается многократно
            // (LoadConversations — на каждое обновление списка, EnsureDmScrollHook — на
            // каждое открытие чата). Без неё плодились лишние скроллеры со своими
            // таймерами и перехватчики сообщений — лишняя нагрузка и рывки.
            if (!_attached.Add(p)) return;
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
            _scrollers[p] = scroller;
            p.Disposed += (s, e) => { _scrollers.Remove(p); _attached.Remove(p); };
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
        ///
        /// КАДРЫ — ПО ОБНОВЛЕНИЮ ЭКРАНА, ДВИЖЕНИЕ — ПО ВРЕМЕНИ.
        ///
        /// Раньше кадры шли от таймера WinForms: он тикает с шагом системных
        /// часов (15,6 мс), то через 15, то через 31 мс, а сдвиг считался «на тик».
        /// Неровные тики давали неровную скорость — та самая ступенька при
        /// листании. На длинной ленте таймер вдобавок замедляли до 30 кадров.
        /// Теперь кадр рисуется, пока очередь сообщений пуста, и ждёт ближайшего
        /// обновления экрана (DwmFlush) — на мониторе 144 Гц это 144 кадра. А
        /// путь за кадр считается от прошедшего ВРЕМЕНИ: пропущенный кадр не
        /// замедляет движение, а просто делает следующий шаг чуть длиннее.
        /// </summary>
        private sealed class SmoothScroller
        {
            private const int StepPx = 110;      // прокрутка за один щелчок колеса
            // Как быстро позиция догоняет цель: доля оставшегося пути за секунду
            // (по экспоненте). Щелчок проходится за ~0,2 с — мягко, но без
            // «киселя»; длинная серия щелчков разгоняет ленту.
            private const double Rate = 12.0;
            // Хвост замедления не ползёт: не медленнее стольких точек в секунду.
            private const double MinSpeed = 260.0;
            // Запасной путь (нет композиции окон): кадры от таймера.
            private const int FallbackFrameMs = 10;

            private readonly Panel _p;
            private readonly System.Windows.Forms.Timer _t;
            private readonly Action _onScrolled;
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private int _target;
            private double _pos;                    // точная позиция, с долями точки
            private double _lastFrame;
            private bool _running;
            private bool _idleHooked;
            private int _lastSet = int.MinValue;   // что выставили мы сами
            // Цель — «в самый низ». Тогда её нельзя запоминать числом: пока идёт
            // прокрутка, лента ещё растёт (дорисовываются картинки, подгружаются
            // аватары — пузыри становятся выше), и низ уезжает дальше. Со старой
            // целью прокрутка останавливалась там, где низ был В МОМЕНТ ЩЕЛЧКА, и
            // до конца не доходила. С этим флагом цель каждый кадр берётся заново.
            private bool _toEnd;

            public SmoothScroller(Panel p, Action onScrolled)
            {
                _p = p;
                _onScrolled = onScrolled;
                _t = new System.Windows.Forms.Timer { Interval = FallbackFrameMs };
                _t.Tick += (s, e) => Frame();
                p.Disposed += (s, e) =>
                {
                    try { _t.Stop(); _t.Dispose(); } catch { }
                    Unhook();
                };
            }

            /// <summary>
            /// Докуда вообще можно прокрутить.
            ///
            /// Считать это как «высота содержимого минус высота видимой части»
            /// НЕДОСТАТОЧНО. Горизонтальную полосу мы прячем вызовом ShowScrollBar,
            /// от которого Windows отдаёт клиентскую область на её высоту больше, —
            /// а WinForms про это не знает и свой предел прокрутки считает по своей,
            /// уменьшенной. В итоге ползунком мышью лента дотягивалась до конца, а
            /// колесом останавливалась на полосу выше: последнее сообщение
            /// оставалось подрезанным снизу.
            ///
            /// Поэтому берём БОЛЬШИЙ из двух пределов — свой и тот, по которому
            /// живёт сама полоса прокрутки (её максимум минус страница). Промах
            /// вверх безопасен: WinForms подрежет позицию сам, а кадр, который
            /// ничего не сдвинул, мы считаем приездом (см. Frame).
            /// </summary>
            private int MaxOffset
            {
                get
                {
                    int byRect = _p.DisplayRectangle.Height - _p.ClientSize.Height;
                    int byBar = 0;
                    try
                    {
                        var v = _p.VerticalScroll;
                        if (v != null) byBar = v.Maximum - v.LargeChange + 1;
                    }
                    catch { }
                    return Math.Max(0, Math.Max(byRect, byBar));
                }
            }
            private int Current => -_p.AutoScrollPosition.Y;

            /// <summary>Остановить прокрутку там, где она сейчас.</summary>
            public void Cancel()
            {
                Stop();
                _toEnd = false;
                _lastSet = int.MinValue;
                _target = Current;
                _animating.Remove(_p);
            }

            /// <summary>Над лентой вставили содержимое — цель едет вместе с ним.</summary>
            public void ShiftBy(int dy)
            {
                if (!_running) return;
                _target += dy;
                _pos += dy;
                if (_lastSet != int.MinValue) _lastSet += dy;
            }
            private int Clamp(int v) => Math.Max(0, Math.Min(MaxOffset, v));

            /// <summary>Щелчок колеса. true — сообщение обработано, панели его не отдаём.
            /// Щелчки НАКАПЛИВАЮТСЯ: в ту же сторону — цель дальше (прокрутка разгоняется),
            /// в обратную — цель возвращается назад (сначала гасит ход, затем идёт обратно).</summary>
            public bool TryHandleWheel(int delta)
            {
                if (MaxOffset <= 0) return false;   // прокручивать нечего — пусть решает панель

                if (!_running) { _target = Current; _pos = Current; }   // старт с текущей позиции
                // Шаг — по величине поворота, а не только по знаку: тачпад и колёса
                // с плавным ходом шлют много мелких щелчков, и каждый раньше
                // уносил ленту на полный шаг — она улетала.
                int px = (int)Math.Round(delta / 120.0 * StepPx);
                if (px == 0) px = Math.Sign(delta);
                _target = Clamp(_target - px);                   // накопление
                _toEnd = _target >= MaxOffset;                  // целимся ровно в конец
                if (!_running)
                {
                    _running = true;
                    _lastSet = Current;
                    _lastFrame = _clock.Elapsed.TotalSeconds;
                    _animating.Add(_p);                 // прячем перекрывающие окна
                    try { _onScrolled?.Invoke(); } catch { }
                    if (FramePacer.Available)
                    {
                        if (!_idleHooked) { Application.Idle += OnIdle; _idleHooked = true; }
                    }
                    else _t.Start();
                }
                return true;
            }

            /// <summary>
            /// Кадры, пока очередь сообщений пуста. Пришло что-то (следующий
            /// щелчок колеса, нажатие, таймер) — выходим, окно его обработает и
            /// снова позовёт нас, когда освободится.
            /// </summary>
            private void OnIdle(object sender, EventArgs e)
            {
                try
                {
                    while (_running && FramePacer.QueueEmpty())
                    {
                        Frame();
                        if (!_running) break;
                        FramePacer.WaitForVBlank();
                    }
                }
                catch { Finish(); }
            }

            private void Unhook()
            {
                if (!_idleHooked) return;
                _idleHooked = false;
                try { Application.Idle -= OnIdle; } catch { }
            }

            private void Stop()
            {
                _running = false;
                try { _t.Stop(); } catch { }
                Unhook();
            }

            /// <summary>Останавливает анимацию и возвращает перекрывающие окна (кнопка «вниз»).</summary>
            private void Finish()
            {
                Stop();
                _toEnd = false;
                _animating.Remove(_p);
                try { _onScrolled?.Invoke(); } catch { }
            }

            private void Frame()
            {
                if (!_running) return;
                if (_p.IsDisposed || !_p.IsHandleCreated) { Finish(); return; }

                double now = _clock.Elapsed.TotalSeconds;
                // Время с прошлого кадра. Сверху подрезаем: после долгой паузы
                // (окно было занято) лента не должна прыгать разом.
                double dt = Math.Max(0.001, Math.Min(0.05, now - _lastFrame));
                _lastFrame = now;

                int cur = Current;
                // Позицию сдвинул кто-то другой (перерисовка ленты / догрузка / ползунок).
                // Обычно это повод прекратить анимацию, чтобы не дёргать взад-вперёд.
                // Но если мы едем в самый низ, прекращать нельзя: перерисовка ленты
                // как раз и восстанавливает позицию, и прокрутка замирала посреди
                // пути. Просто продолжаем с того места, куда нас поставили.
                if (_lastSet != int.MinValue && Math.Abs(cur - _lastSet) > 2)
                {
                    if (!_toEnd) { Finish(); return; }
                    _lastSet = cur;
                    _pos = cur;
                }

                _target = _toEnd ? MaxOffset : Clamp(_target);
                double diff = _target - _pos;
                if (Math.Abs(diff) < 0.5)
                {
                    if (cur != _target)
                    {
                        try { _p.AutoScrollPosition = new Point(0, _target); } catch { }
                        RepaintNow(_p);
                    }
                    Finish();                       // снимаем флаг → кнопка «вниз» вернётся
                    return;
                }

                // Догоняем цель по экспоненте от прошедшего времени. Минимальная
                // скорость делает конец движения коротким: без неё последние
                // точки ползли бы кадр за кадром, и любая помеха оставляла ленту
                // не доехавшей.
                double step = diff * (1 - Math.Exp(-Rate * dt));
                double minStep = MinSpeed * dt;
                if (Math.Abs(step) < minStep) step = Math.Sign(diff) * Math.Min(minStep, Math.Abs(diff));
                _pos += step;

                int want = (int)Math.Round(_pos);
                if (want == cur) return;            // доля точки — двигать пока нечего
                try { _p.AutoScrollPosition = new Point(0, want); } catch { }
                int got = Current;
                // Просили сдвинуть, а лента не сдвинулась — значит приехали, дальше
                // некуда. Это и страховка от завышенной цели: без неё кадры крутились
                // бы вхолостую, снова и снова выставляя одно и то же значение.
                if (got == cur) { Finish(); return; }
                if (got != want) _pos = got;
                // Кадр двигает позицию напрямую и НЕ порождает WM_VSCROLL/WM_MOUSEWHEEL,
                // поэтому открывшуюся полоску дорисовываем сами — сразу, в этом кадре.
                RepaintNow(_p);
                _lastSet = got;   // фактическое (могло быть подрезано)
                try { _onScrolled?.Invoke(); } catch { }   // пагинация и кнопка «вниз»
            }
        }

        /// <summary>
        /// Темп кадров по обновлению экрана.
        ///
        /// DwmFlush ждёт ближайшего кадра композитора — то есть ровно того
        /// момента, когда нарисованное попадёт на экран. Рисовать чаще
        /// бессмысленно (лишнее просто не покажут), реже — заметно глазу.
        /// </summary>
        private static class FramePacer
        {
            [DllImport("dwmapi.dll")] private static extern int DwmFlush();
            [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);
            [DllImport("user32.dll")]
            private static extern bool PeekMessage(out NativeMsg msg, IntPtr hWnd, uint min, uint max, uint remove);

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeMsg
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int ptX;
                public int ptY;
            }

            private static bool? _available;
            private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

            /// <summary>Есть ли композиция окон (с Windows 8 — всегда).</summary>
            public static bool Available
            {
                get
                {
                    if (_available == null)
                    {
                        try { _available = DwmIsCompositionEnabled(out bool on) == 0 && on; }
                        catch { _available = false; }
                    }
                    return _available.Value;
                }
            }

            /// <summary>Нет ли сообщений, которые окну пора обработать.</summary>
            public static bool QueueEmpty()
            {
                try { return !PeekMessage(out _, IntPtr.Zero, 0, 0, 0); }
                catch { return false; }
            }

            public static void WaitForVBlank()
            {
                long t0 = _sw.ElapsedTicks;
                int hr = -1;
                try { hr = DwmFlush(); } catch { _available = false; }
                double ms = (_sw.ElapsedTicks - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                // Вернулся сразу (композиция недоступна или показывать было нечего) —
                // не крутимся вхолостую на всё ядро.
                if (hr != 0 || ms < 1.5) System.Threading.Thread.Sleep(4);
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
        private const uint RDW_FRAME = 0x0400;
        private const uint RDW_NOERASE = 0x0020;

        /// <summary>
        /// Перерисовка после СМЕНЫ содержимого ленты — со стиранием фона.
        ///
        /// Обычный RepaintNow фон намеренно НЕ стирает (RDW_ERASE там нет): при
        /// прокрутке это давало бы мигание на каждом кадре. Но при смене чата
        /// нужно ровно обратное. Панель держит WS_CLIPCHILDREN, то есть родитель
        /// не рисует под дочерними окнами; пузыри прошлого чата удаляются, новые
        /// встают на другие места, и там, где под старым пузырём фон никто не
        /// перерисовал, остаётся прежняя картинка — сообщения внахлёст и обрывки
        /// изображений.
        ///
        /// Смена чата случается по нажатию, а не шестьдесят раз в секунду, так
        /// что полное стирание с последующей отрисовкой здесь ничего не стоит.
        /// </summary>
        public static void RepaintAfterSwitch(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch { }
        }

        /// <summary>
        /// Дорисовать то, что открылось при прокрутке, — сразу, в этом же кадре.
        ///
        /// РАНЬШЕ НА КАЖДОМ КАДРЕ ПЕРЕРИСОВЫВАЛАСЬ ВСЯ ЛЕНТА: панель объявлялась
        /// недействительной целиком, со всеми пузырями, и каждый видимый пузырь —
        /// текст, картинки, превью роликов — рисовался заново шестьдесят раз в
        /// секунду. Это и было главной просадкой: чем больше на экране картинок
        /// и чем длиннее лента, тем ниже кадры. На длинной ленте пузыри вдобавок
        /// пропускали, и посреди прокрутки могли оставаться недорисованные полосы.
        ///
        /// Но содержимое при прокрутке и так переезжает целиком: Windows сдвигает
        /// уже нарисованное (ScrollWindowEx) и помечает недействительной только
        /// открывшуюся полоску. Её и дорисовываем — синхронно, вместе с теми
        /// пузырями, которые в неё въехали. Артефактов «не успела отрисоваться»
        /// это не возвращает: дорисовка всё так же идёт сразу, а не в очередь.
        /// </summary>
        public static void RepaintNow(Control c) => RepaintNow(c, false);

        /// <param name="force">Перерисовать ВСЁ видимое, а не только открывшееся.</param>
        public static void RepaintNow(Control c, bool force)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;

                uint flags = force
                    ? RDW_INVALIDATE | RDW_NOERASE | RDW_UPDATENOW | RDW_ALLCHILDREN
                    : RDW_UPDATENOW | RDW_ALLCHILDREN;
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, flags);

                var parent = c.Parent;
                if (parent == null) return;
                foreach (Control sib in parent.Controls)
                {
                    if (ReferenceEquals(sib, c) || !sib.Visible || !sib.IsHandleCreated) continue;
                    if (!sib.Bounds.IntersectsWith(c.Bounds)) continue;   // только перекрывающие
                    // БЕЗ UPDATENOW: соседу (кнопка «вниз») достаточно пометить область
                    // недействительной — он перерисуется в обычном цикле. Синхронная
                    // отрисовка соседа на каждом кадре как раз и давала фриз, когда
                    // кнопка появлялась.
                    RedrawWindow(sib.Handle, IntPtr.Zero, IntPtr.Zero,
                        RDW_INVALIDATE | RDW_NOERASE | RDW_ALLCHILDREN);
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

        /// <summary>
        /// Заморозить отрисовку панели на время пересборки ленты.
        ///
        /// Раньше эти два метода были пустыми: WM_SETREDRAW пробовали и убрали,
        /// потому что после разморозки оставались рваные, наполовину отрисованные
        /// пузыри. Причина была не в самой заморозке, а в том, что после неё никто
        /// не перерисовывал панель ЦЕЛИКОМ — со стиранием фона и вместе с детьми.
        /// Теперь это делает ResumeDraw.
        ///
        /// Зачем это вообще нужно. Сборка страницы занимает 130 мс, и всё это
        /// время панель уже пуста: старые пузыри удалены, новые ещё не добавлены.
        /// Любая перерисовка в этот промежуток показывает пустоту — именно она и
        /// мелькала при переходе между чатами. С замороженной отрисовкой ни одного
        /// промежуточного кадра не появляется: лента сменяется одним движением.
        /// </summary>
        public static void SuspendDraw(Control c)
        {
            try { if (c != null && c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, false, 0); }
            catch { }
        }

        /// <summary>Разморозить и показать собранное — одним полным перерисовыванием.</summary>
        public static void ResumeDraw(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                SendMessage(c.Handle, WM_SETREDRAW, true, 0);
                // Пока отрисовка была заморожена, Windows не накапливает недействительные
                // области — поэтому просто «разморозить» мало, нужно явно перерисовать
                // всё: фон, детей и рамку (полоса прокрутки — не клиентская область,
                // без RDW_FRAME она осталась бы от прошлого чата).
                RepaintAfterSwitch(c);
            }
            catch { }
        }

    }
}
