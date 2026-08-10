using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Всплывающий календарь для перехода к сообщениям за конкретную дату.
    /// Общий для мессенджера (ЛС/группы) и серверных каналов: показывается под
    /// кнопкой 📅 в строке поиска, при выборе даты вызывает колбэк и закрывается.
    /// </summary>
    public static class DatePickerPopup
    {
        /// <summary>Показывает календарь под указанным контролом.</summary>
        /// <param name="anchor">Кнопка, под которой раскрывается календарь.</param>
        /// <param name="maxDate">Верхняя граница выбора (обычно сегодня).</param>
        /// <param name="onPicked">Вызывается с выбранной датой (без времени).</param>
        public static void Show(Control anchor, DateTime? maxDate, Action<DateTime> onPicked)
        {
            if (anchor == null || onPicked == null) return;

            var popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(40, 42, 46),
                Padding = new Padding(1)
            };

            var cal = new MonthCalendar
            {
                MaxSelectionCount = 1,
                ShowToday = true,
                ShowTodayCircle = true,
                Location = new Point(1, 1),
                // Цвета применяются не во всех темах Windows, но в тёмном режиме
                // приложения календарь и так рисуется тёмным.
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.FromArgb(220, 221, 222),
                TitleBackColor = Color.FromArgb(88, 101, 242),
                TitleForeColor = Color.White,
                TrailingForeColor = Color.FromArgb(120, 122, 128)
            };
            if (maxDate.HasValue) { try { cal.MaxDate = maxDate.Value.Date; } catch { } }

            popup.Controls.Add(cal);
            popup.ClientSize = new Size(cal.Width + 2, cal.Height + 2);

            // Позиционируем под кнопкой, но не выпуская за край экрана.
            try
            {
                var pt = anchor.PointToScreen(new Point(0, anchor.Height + 2));
                var wa = Screen.FromControl(anchor).WorkingArea;
                int x = Math.Min(pt.X, wa.Right - popup.Width - 4);
                int y = pt.Y + popup.Height > wa.Bottom
                    ? anchor.PointToScreen(Point.Empty).Y - popup.Height - 2   // не влезает вниз — раскрываем вверх
                    : pt.Y;
                popup.Location = new Point(Math.Max(wa.Left + 4, x), Math.Max(wa.Top + 4, y));
            }
            catch { popup.StartPosition = FormStartPosition.CenterParent; }

            void Pick(DateTime d)
            {
                try { popup.Close(); } catch { }
                try { onPicked(d.Date); } catch { }
            }

            cal.DateSelected += (s, e) => Pick(e.Start);
            popup.Deactivate += (s, e) => { try { popup.Close(); } catch { } };
            popup.KeyPreview = true;
            popup.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { popup.Close(); } catch { } } };
            popup.FormClosed += (s, e) => { try { popup.Dispose(); } catch { } };

            try { popup.Show(anchor.FindForm()); popup.Activate(); } catch { }
        }
    }
}
