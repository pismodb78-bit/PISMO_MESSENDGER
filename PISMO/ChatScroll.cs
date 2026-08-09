using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Прокрутка чатов/списков: убираем только ГОРИЗОНТАЛЬНУЮ нативную полосу.
    /// Вертикальная — обычная нативная (кастомный тонкий ползунок убран по просьбе:
    /// в этой раскладке он стабильно не выходил без живой отладки).
    /// Методы-заглушки сохранены для совместимости вызовов.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
        private const int SB_HORZ = 0;
        private const int WM_SETREDRAW = 0x000B;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();

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

        /// <summary>Убирает горизонтальный скролл; вертикальный остаётся нативным.</summary>
        public static void Attach(Panel p) => KillHorizontal(p);
        public static void AttachChat(Panel p) => KillHorizontal(p);

        // Заглушки — вертикальную полосу больше не трогаем.
        public static void ApplyDarkScrollbar(Control c) { }
        public static void EnableAppDarkMode() { }
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

        /// <summary>Замораживает отрисовку на время массовой пересборки ленты (без мигания).</summary>
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
    }
}
