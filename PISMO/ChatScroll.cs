using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл (вертикальный оставляем
    /// нативным — он работает надёжно во всех режимах окна).
    ///
    /// Важно: кастомный вертикальный ползунок-оверлей был удалён намеренно. В этой
    /// раскладке (докнутые панели, FlowLayoutPanel, встроенная ServersForm) он ни
    /// в одном варианте (прозрачный/непрозрачный/на форме) не рисовался стабильно
    /// и ломал прокрутку в чатах. Нативный вертикальный скролл — единственный
    /// надёжный вариант без живого тестирования на машине.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0;

        private static readonly System.Collections.Generic.HashSet<Panel> _hkill = new();

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
        /// Раньше вешал кастомный тонкий ползунок. Теперь только убирает горизонтальный
        /// скролл — вертикальный остаётся нативным. Сигнатура сохранена, чтобы не менять
        /// вызовы в MainForm/ServersForm.
        /// </summary>
        public static void Attach(Panel p) => KillHorizontal(p);
    }
}
