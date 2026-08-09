using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Прокрутка чатов/списков:
    ///  • горизонтальную нативную полосу прячем (ShowScrollBar);
    ///  • вертикальную оставляем НАТИВНОЙ, но в тёмной теме переводим её в тёмный
    ///    стиль Windows (SetWindowTheme "DarkMode_Explorer") — она становится тёмной
    ///    и тонкой (в Win11), не белой; стоит на штатном месте (не лезет на кнопку
    ///    звонка) и не мерцает.
    ///
    /// Самодельный оверлей-ползунок убран намеренно: в этой раскладке (докнутые
    /// панели + FlowLayoutPanel + встроенный ServersForm) он не рисовался стабильно
    /// и давал баги (белая полоса не перекрывалась, «крышебойный» над кнопкой,
    /// мерцание). Тёмная нативная полоса — надёжный результат без живой отладки.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private const int SB_HORZ = 0;
        private const int WM_SETREDRAW = 0x000B;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();
        private static readonly System.Collections.Generic.HashSet<Panel> _pretty = new();

        /// <summary>Прячет ГОРИЗОНТАЛЬНУЮ нативную полосу (в т.ч. в оконном режиме).</summary>
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

        /// <summary>Включает двойную буферизацию (protected-свойство Panel) — меньше смаза.</summary>
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

        /// <summary>Убирает горизонтальный скролл, включает буферизацию и тёмную
        /// нативную вертикальную полосу (в тёмной теме).</summary>
        public static void Attach(Panel p)
        {
            if (p == null || _pretty.Contains(p)) return;
            _pretty.Add(p);
            KillHorizontal(p);
            EnableDoubleBuffer(p);
            p.Scroll += (s, e) => p.Invalidate(true);
            p.MouseWheel += (s, e) => p.Invalidate(true);

            void ApplyTheme()
            {
                try
                {
                    if (!p.IsHandleCreated) return;
                    bool light = false;
                    try { light = Theme.IsLight; } catch { }
                    // В тёмной теме — тёмная полоса; в светлой — стандартная светлая.
                    SetWindowTheme(p.Handle, light ? "Explorer" : "DarkMode_Explorer", null);
                }
                catch { }
            }
            p.HandleCreated += (s, e) => ApplyTheme();
            ApplyTheme();
        }

        /// <summary>Совместимость: то же, что <see cref="Attach(Panel)"/>.</summary>
        public static void AttachChat(Panel p) => Attach(p);

        /// <summary>Замораживает отрисовку контрола (перед массовой пересборкой ленты),
        /// чтобы не было мигания «загрузки заново».</summary>
        public static void SuspendDraw(Control c)
        {
            try { if (c != null && c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, false, 0); } catch { }
        }

        /// <summary>Размораживает отрисовку и перерисовывает контрол целиком.</summary>
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
    }
}
