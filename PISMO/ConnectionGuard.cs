using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Сторож соединения с БД. Когда любое DBHelper.OpenConnection падает с сетевой
    /// ошибкой — показывает поверх приложения окно «Нет связи с сервером…» и сам
    /// пингует БД, пока связь не восстановится (тогда окно скрывается). Так
    /// приложение не «зависает молча», а показывает понятный статус.
    /// </summary>
    public static class ConnectionGuard
    {
        private static Form _owner;
        private static System.Threading.SynchronizationContext _ui;
        private static Form _overlay;
        private static Label _lbl;
        private static Panel _spinner;
        private static double _angle;
        private static System.Windows.Forms.Timer _spinTimer;
        private static System.Windows.Forms.Timer _retryTimer;
        private static volatile bool _lost;

        /// <summary>Вызвать один раз из главной формы (на UI-потоке).</summary>
        public static void Init(Form owner)
        {
            _owner = owner;
            _ui = System.Threading.SynchronizationContext.Current;
        }

        /// <summary>Соединение успешно — спрятать окно (если было показано).</summary>
        public static void NotifyOk()
        {
            if (!_lost) return;
            _lost = false;
            Post(HideOverlay);
        }

        /// <summary>Соединение потеряно — показать окно и запустить переподключение.</summary>
        public static void NotifyLost()
        {
            if (_lost) return;
            _lost = true;
            Post(ShowOverlay);
        }

        private static void Post(Action a)
        {
            try
            {
                if (_owner != null && !_owner.IsDisposed && _owner.IsHandleCreated)
                {
                    if (_owner.InvokeRequired) _owner.BeginInvoke(new Action(() => { try { a(); } catch { } }));
                    else a();
                }
                else if (_ui != null) _ui.Post(_ => { try { a(); } catch { } }, null);
            }
            catch { }
        }

        private static void BuildOverlay()
        {
            _overlay = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(30, 31, 34),
                ClientSize = new Size(340, 150),
                TopMost = true
            };
            _spinner = new Panel { Size = new Size(48, 48), Location = new Point(146, 22), BackColor = Color.Transparent };
            _spinner.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(4, 4, 40, 40);
                using var track = new Pen(Color.FromArgb(80, 255, 255, 255), 5);
                using var arc = new Pen(Color.FromArgb(88, 101, 242), 5);
                e.Graphics.DrawEllipse(track, rect);
                e.Graphics.DrawArc(arc, rect, (float)_angle, 110);
            };
            _lbl = new Label
            {
                Text = "Нет связи с сервером.\nПереподключение…",
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 82), Size = new Size(320, 56)
            };
            _overlay.Controls.Add(_spinner);
            _overlay.Controls.Add(_lbl);
            // Рамка-акцент.
            _overlay.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(88, 101, 242), 2);
                e.Graphics.DrawRectangle(pen, 1, 1, _overlay.Width - 3, _overlay.Height - 3);
            };

            _spinTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _spinTimer.Tick += (s, e) => { _angle = (_angle + 24) % 360; try { _spinner.Invalidate(); } catch { } };

            _retryTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _retryTimer.Tick += (s, e) => PingOnce();
        }

        private static void ShowOverlay()
        {
            if (_owner == null || _owner.IsDisposed) return;
            if (_overlay == null || _overlay.IsDisposed) BuildOverlay();
            try { _owner.Enabled = false; } catch { }
            if (!_overlay.Visible)
            {
                try { _overlay.Show(_owner); } catch { try { _overlay.Show(); } catch { } }
            }
            try { _overlay.BringToFront(); } catch { }
            _spinTimer.Start();
            _retryTimer.Start();
            PingOnce();
        }

        private static void HideOverlay()
        {
            try { _retryTimer?.Stop(); } catch { }
            try { _spinTimer?.Stop(); } catch { }
            try { _owner.Enabled = true; } catch { }
            try { if (_overlay != null && _overlay.Visible) _overlay.Hide(); } catch { }
        }

        // Пробуем открыть соединение в фоне; успех сам вызовет NotifyOk (внутри OpenConnection).
        private static void PingOnce()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { using var c = DBHelper.OpenConnection(); }
                catch { /* ещё нет связи — ждём следующего тика */ }
            });
        }
    }
}
