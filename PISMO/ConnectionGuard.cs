using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Сторож соединения с БД. Когда любое DBHelper.OpenConnection падает с сетевой
    /// ошибкой — поверх содержимого главного окна показывается «заставка»
    /// «Нет связи с сервером…», а сам сторож пингует БД, пока связь не восстановится.
    ///
    /// ВАЖНО: это НЕ отдельное окно и НЕ TopMost — это панель внутри самого MainForm
    /// (перекрывает весь клиентский прямоугольник). Поэтому окно остаётся обычным:
    /// его можно свернуть/подвинуть, размер не меняется, а взаимодействие с
    /// интерфейсом под панелью заблокировано тем, что панель его перекрывает.
    /// </summary>
    public static class ConnectionGuard
    {
        private static Form _owner;
        private static System.Threading.SynchronizationContext _ui;
        private static Panel _panel;
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

        /// <summary>Соединение успешно — спрятать заставку (если была показана).</summary>
        public static void NotifyOk()
        {
            if (!_lost) return;
            _lost = false;
            Post(HideOverlay);
        }

        /// <summary>Соединение потеряно — показать заставку и запустить переподключение.</summary>
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

        private static void BuildPanel()
        {
            _panel = new Panel
            {
                BackColor = Color.FromArgb(30, 31, 34),
                Visible = false
            };
            _panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                int cx = _panel.ClientSize.Width / 2;
                int cy = _panel.ClientSize.Height / 2;

                // Спиннер.
                var rect = new Rectangle(cx - 22, cy - 46, 44, 44);
                using (var track = new Pen(Color.FromArgb(80, 255, 255, 255), 5))
                using (var arc = new Pen(Color.FromArgb(88, 101, 242), 5))
                {
                    g.DrawEllipse(track, rect);
                    g.DrawArc(arc, rect, (float)_angle, 110);
                }

                // Текст.
                using var font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
                using var sub = new Font("Segoe UI", 9f);
                TextRenderer.DrawText(g, "Нет связи с сервером", font,
                    new Rectangle(0, cy + 8, _panel.ClientSize.Width, 24),
                    Color.FromArgb(230, 231, 232),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                TextRenderer.DrawText(g, "Переподключение…", sub,
                    new Rectangle(0, cy + 34, _panel.ClientSize.Width, 20),
                    Color.FromArgb(150, 152, 158),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            };

            _spinTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _spinTimer.Tick += (s, e) => { _angle = (_angle + 24) % 360; try { _panel.Invalidate(); } catch { } };

            _retryTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _retryTimer.Tick += (s, e) => PingOnce();
        }

        private static void ShowOverlay()
        {
            if (_owner == null || _owner.IsDisposed) return;
            if (_panel == null || _panel.IsDisposed) BuildPanel();

            if (_panel.Parent != _owner)
            {
                try { _owner.Controls.Add(_panel); } catch { }
            }
            // Перекрываем весь клиентский прямоугольник и держим сверху.
            try
            {
                _panel.Bounds = _owner.ClientRectangle;
                _panel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                _panel.Visible = true;
                _panel.BringToFront();
            }
            catch { }

            _spinTimer.Start();
            _retryTimer.Start();
            PingOnce();
        }

        private static void HideOverlay()
        {
            try { _retryTimer?.Stop(); } catch { }
            try { _spinTimer?.Stop(); } catch { }
            try { if (_panel != null && !_panel.IsDisposed) _panel.Visible = false; } catch { }
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
