using System;
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

        /// <summary>Убирает горизонтальный скролл и делает вертикальную нативную полосу тёмной.</summary>
        public static void Attach(Panel p)
        {
            if (p == null) return;
            KillHorizontal(p);
            EnableAppDarkMode();
            void Apply() => ApplyDarkScrollbar(p);
            p.HandleCreated += (s, e) => Apply();
            p.Resize += (s, e) => Apply();
            p.VisibleChanged += (s, e) => Apply();
            Apply();

            // Артефакты при быстрой прокрутке = отставание перерисовки: Invalidate лишь
            // ставит WM_PAINT в очередь, и следующий шаг прокрутки приходит раньше, чем
            // очередь разгребётся («не успевает отрисоваться»). RedrawWindow с
            // UPDATENOW|ALLCHILDREN перерисовывает панель И все пузыри СИНХРОННО.
            p.Scroll += (s, e) => RepaintNow(p);
            p.MouseWheel += (s, e) => RepaintNow(p);
        }

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprc, IntPtr hrgn, uint flags);
        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;

        /// <summary>Немедленная (синхронная) перерисовка контрола вместе со всеми дочерними.</summary>
        public static void RepaintNow(Control c)
        {
            try
            {
                if (c == null || !c.IsHandleCreated) return;
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch { }
        }

        /// <summary>Для панелей сообщений: как Attach + двойная буферизация.
        /// WS_CLIPCHILDREN НЕ трогаем — его снятие рвало отрисовку пузырей.</summary>
        public static void AttachChat(Panel p)
        {
            Attach(p);
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
