using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Убирает у AutoScroll-панели ГОРИЗОНТАЛЬНЫЙ скролл (насовсем). Вертикальный
    /// оставляем нативным — он даёт корректный отступ (контент не лезет за край)
    /// и не мерцает. Кастомный тонкий ползунок пробовали, но он перекрывал
    /// сообщения и мигал — откатили.
    /// </summary>
    public static class ChatScroll
    {
        [DllImport("user32.dll")] private static extern int ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        private const int SB_HORZ = 0;

        private static readonly System.Collections.Generic.HashSet<Panel> _done = new();

        /// <summary>Убрать горизонтальный скролл у панели (везде — чаты, сайдбары, списки).</summary>
        public static void KillHorizontal(Panel p)
        {
            if (p == null || _done.Contains(p)) return;
            _done.Add(p);
            p.HorizontalScroll.Enabled = false;
            void Hide() { try { if (p.IsHandleCreated) ShowScrollBar(p.Handle, SB_HORZ, false); } catch { } }
            p.Layout += (s, e) => Hide();
            p.Resize += (s, e) => Hide();
            p.Scroll += (s, e) => Hide();
            p.ControlAdded += (s, e) => Hide();
            Hide();
        }

        /// <summary>Совместимость: то же, что KillHorizontal.</summary>
        public static void Attach(Panel p) => KillHorizontal(p);
    }
}
