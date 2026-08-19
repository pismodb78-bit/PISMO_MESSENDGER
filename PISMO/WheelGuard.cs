using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Глобальный перехват колеса мыши: не даёт менять значение контролов, над
    /// которыми крутят колесо (ComboBox / TrackBar / NumericUpDown / DateTimePicker) —
    /// раньше можно было случайно поменять устройство/громкость/качество, просто
    /// прокрутив над ними страницу. Вместо изменения значения прокручиваем ближайший
    /// скроллируемый контейнер (страница листается как обычно).
    /// </summary>
    internal sealed class WheelGuard : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            try
            {
                var cur = Cursor.Position;
                IntPtr hwnd = WindowFromPoint(new Point(cur.X, cur.Y));
                if (hwnd == IntPtr.Zero) return false;
                var c = Control.FromChildHandle(hwnd) ?? Control.FromHandle(hwnd);
                if (c == null) return false;

                // Ищем «значимый» контрол под курсором.
                Control ctl = c;
                while (ctl != null && !(ctl is ComboBox || ctl is TrackBar ||
                                        ctl is NumericUpDown || ctl is DateTimePicker))
                    ctl = ctl.Parent;
                if (ctl == null) return false;   // обычный контрол — не мешаем

                // Прокручиваем ближайший скроллируемый контейнер вместо смены значения.
                Control scroll = ctl.Parent;
                while (scroll != null &&
                       !(scroll is ScrollableControl sc && sc.AutoScroll && sc.VerticalScroll.Visible))
                    scroll = scroll.Parent;
                if (scroll != null)
                    SendMessage(scroll.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);

                return true;   // блокируем изменение значения контрола
            }
            catch { return false; }
        }
    }
}
