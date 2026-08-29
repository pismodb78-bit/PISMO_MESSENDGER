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
            // Чем меньше Ease, тем мягче: за кадр проходим лишь малую долю оставшегося
            // пути, поэтому сдвиг между кадрами крошечный и движение «маслянистое».
            private const double Ease = 0.09;
            private const int MinFrameMs = 15;   // ~65 кадров/с
            private const int MinStepPx = 6;     // минимальный сдвиг за кадр — чтобы хвост не полз

            private readonly Panel _p;
            private readonly System.Windows.Forms.Timer _t;
            private readonly Action _onScrolled;
            private int _target;
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
                _t = new System.Windows.Forms.Timer { Interval = MinFrameMs };
                _t.Tick += Tick;
                p.Disposed += (s, e) => { try { _t.Stop(); _t.Dispose(); } catch { } };
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
            /// ничего не сдвинул, мы считаем приездом (см. Tick).
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
            private int Clamp(int v) => Math.Max(0, Math.Min(MaxOffset, v));

            /// <summary>Щелчок колеса. true — сообщение обработано, панели его не отдаём.
            /// Щелчки НАКАПЛИВАЮТСЯ: в ту же сторону — цель дальше (прокрутка разгоняется),
            /// в обратную — цель возвращается назад (сначала гасит ход, затем идёт обратно).</summary>
            public bool TryHandleWheel(int delta)
            {
                if (MaxOffset <= 0) return false;   // прокручивать нечего — пусть решает панель

                if (!_t.Enabled) _target = Current;              // старт с текущей позиции
                _target = Clamp(_target - Math.Sign(delta) * StepPx);   // накопление
                _toEnd = _target >= MaxOffset;                  // целимся ровно в конец
                if (!_t.Enabled)
                {
                    _lastSet = Current;
                    _animating.Add(_p);                 // прячем перекрывающие окна
                    try { _onScrolled?.Invoke(); } catch { }
                    _t.Start();
                }
                return true;
            }

            /// <summary>Останавливает анимацию и возвращает перекрывающие окна (кнопка «вниз»).</summary>
            private void Finish()
            {
                _t.Stop();
                _toEnd = false;
                _animating.Remove(_p);
                try { _onScrolled?.Invoke(); } catch { }
            }

            private void Tick(object sender, EventArgs e)
            {
                if (_p.IsDisposed || !_p.IsHandleCreated) { Finish(); return; }

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
                }

                _target = _toEnd ? MaxOffset : Clamp(_target);
                int diff = _target - cur;
                if (Math.Abs(diff) <= 1)
                {
                    try { _p.AutoScrollPosition = new Point(0, _target); } catch { }
                    RepaintNow(_p, force: true);   // финальный кадр — обязательно
                    Finish();                       // снимаем флаг → кнопка «вниз» вернётся
                    return;
                }

                // Замедление к концу приятно смотрится, но хвост у него бесконечный:
                // последние десятки пикселей ползли кадр за кадром по одному, и любая
                // помеха оставляла ленту не доехавшей. Минимальный шаг делает конец
                // движения коротким и предсказуемым, не портя саму плавность.
                int step = (int)Math.Round(diff * Ease);
                if (Math.Abs(step) < MinStepPx) step = Math.Sign(diff) * Math.Min(MinStepPx, Math.Abs(diff));
                try { _p.AutoScrollPosition = new Point(0, cur + step); } catch { }
                // Просили сдвинуть, а лента не сдвинулась — значит приехали, дальше
                // некуда. Это и страховка от завышенной цели: без неё кадры крутились
                // бы вхолостую, снова и снова выставляя одно и то же значение.
                if (Current == cur) { RepaintNow(_p, force: true); Finish(); return; }
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
        // Кнопку «вниз» убрали, второй полной перерисовки на кадр больше нет — можно
        // рисовать на каждом кадре анимации (было ~30/с, из-за чего движение выглядело
        // ступенчатым при мягком easing).
        private const int RepaintMinIntervalMs = 15;   // ~65 синхронных перерисовок/с

        public static void RepaintNow(Control c, bool force)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;

                int now = Environment.TickCount;
                if (!force && unchecked(now - _lastRepaintTick) < RepaintMinIntervalMs) return;
                _lastRepaintTick = now;

                // RDW_NOERASE — против мерцания при прокрутке.
                //
                // Без него каждый кадр прокрутки (до 65 в секунду) заставляет
                // каждого ребёнка сначала залить свой фон, а потом нарисовать
                // содержимое. Для текстовых полей это заметно глазом: буквы
                // моргают. Стирать здесь и не нужно — содержимое перекрывает
                // свою область целиком. Там, где стирание действительно нужно
                // (смена чата, под удалёнными пузырями остаётся прошлое), для
                // этого есть отдельный RepaintAfterSwitch.
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_NOERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);

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
