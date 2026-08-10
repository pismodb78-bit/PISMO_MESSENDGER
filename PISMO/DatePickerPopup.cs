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
            // ВАЖНО: MonthCalendar вычисляет свой настоящий размер только после
            // создания хендла. Раньше окно подгонялось по размеру ДО показа — и
            // календарь оказывался обрезан справа и снизу. Поэтому сначала AutoSize,
            // а точный размер и позицию выставляем уже после Show().
            popup.AutoSize = true;
            popup.AutoSizeMode = AutoSizeMode.GrowAndShrink;

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

            try
            {
                popup.Show(anchor.FindForm());

                // Размер календаря уже известен — фиксируем окно точно по нему.
                popup.AutoSize = false;
                popup.ClientSize = new Size(cal.Width + 2, cal.Height + 2);

                // Раскрываем под кнопкой, не выпуская за границы экрана.
                var below = anchor.PointToScreen(new Point(0, anchor.Height + 2));
                var wa = Screen.FromControl(anchor).WorkingArea;
                int x = Math.Min(below.X, wa.Right - popup.Width - 4);
                int y = below.Y + popup.Height > wa.Bottom
                    ? anchor.PointToScreen(Point.Empty).Y - popup.Height - 2   // вниз не влезает — вверх
                    : below.Y;
                popup.Location = new Point(Math.Max(wa.Left + 4, x), Math.Max(wa.Top + 4, y));

                popup.Activate();
            }
            catch { }

        }
    }
}
