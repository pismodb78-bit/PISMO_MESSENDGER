using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Заставка при запуске (как у Discord): показывается сразу, пока идёт
    /// проверка обновлений и подключение к БД, затем открывает окно входа.
    /// Благодаря ей приложение не выглядит «зависшим» первые секунды.
    /// </summary>
    public sealed class SplashForm : Form
    {
        private readonly ApplicationContext _ctx;
        private readonly Label _status;
        private bool _startedNext;   // окно входа уже открыто

        public SplashForm(ApplicationContext ctx)
        {
            _ctx = ctx;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 240);
            BackColor = Color.FromArgb(30, 31, 34);
            ShowInTaskbar = true;
            DoubleBuffered = true;
            Text = "PISMO";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Скруглённые углы.
            var path = new GraphicsPath();
            int r = 20;
            var rc = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            path.AddArc(rc.X, rc.Y, r, r, 180, 90);
            path.AddArc(rc.Right - r, rc.Y, r, r, 270, 90);
            path.AddArc(rc.Right - r, rc.Bottom - r, r, r, 0, 90);
            path.AddArc(rc.X, rc.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            Region = new Region(path);

            // Логотип (иконка приложения) по центру.
            var logo = new PictureBox
            {
                Size = new Size(72, 72),
                Location = new Point((ClientSize.Width - 72) / 2, 34),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            try
            {
                var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) logo.Image = new Icon(ic, 64, 64).ToBitmap();
            }
            catch { }
            Controls.Add(logo);

            var title = new Label
            {
                Text = "PISMO",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(ClientSize.Width, 32),
                Location = new Point(0, 112),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            Controls.Add(title);

            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var version = new Label
            {
                Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}",
                ForeColor = Color.FromArgb(120, 122, 128),
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = false,
                Size = new Size(ClientSize.Width, 16),
                Location = new Point(0, 144),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            Controls.Add(version);

            _status = new Label
            {
                Text = "Запуск…",
                ForeColor = Color.FromArgb(150, 152, 158),
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                Size = new Size(ClientSize.Width, 20),
                Location = new Point(0, 178),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            Controls.Add(_status);

            var bar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25,
                Size = new Size(220, 6),
                Location = new Point((ClientSize.Width - 220) / 2, 208)
            };
            Controls.Add(bar);

            Shown += async (s, e) =>
            {
                Activate();
                await RunStartupAsync();
            };
        }

        private void SetStatus(string text)
        {
            try { _status.Text = text; _status.Refresh(); } catch { }
        }

        /// <summary>Последовательность запуска: обновления → БД → окно входа.</summary>
        private async Task RunStartupAsync()
        {
            // 1. Проверка обновлений (GitHub Releases). Диалог «обновить?»
            //    показывается поверх заставки; при обновлении приложение закроется.
            SetStatus("Проверка обновлений…");
            bool updating = false;
            try { updating = await Updater.CheckInteractiveAsync(this); } catch { }
            if (updating) return;

            // 2. Прогрев подключения к БД и миграции (friends/user_prefs) — чтобы
            //    окно входа открылось мгновенно и без «подвисания».
            SetStatus("Подключение к базе данных…");
            await Task.Run(() =>
            {
                try { using var c = DBHelper.OpenConnection(); } catch { }
                try { FriendsRepository.EnsureTable(); } catch { }
            });

            // 3. Автовход по сохранённому JWT — тоже в фоне, пока висит заставка.
            //    Если получилось, окно входа даже не показывается (как в Discord).
            SetStatus("Вход…");
            bool restored = false;
            try { restored = await Task.Run(LoginForm.TryRestoreSession); } catch { }

            // 4. Открываем окно входа (или сразу главное) и закрываем заставку.
            SetStatus("Загрузка интерфейса…");
            try
            {
                var login = new LoginForm();
                _ctx.MainForm = login;   // теперь жизнь приложения привязана к окну входа
                _startedNext = true;
                if (restored) login.OpenMainAfterRestore(); // окно входа остаётся скрытым
                else login.Show();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось запустить приложение:\n" + ex.Message,
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close(); // _startedNext=false → выйдем из приложения
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Заставку закрыли до открытия окна входа (Alt+F4) — выходим,
            // иначе цикл сообщений останется висеть без окон.
            if (!_startedNext) Application.ExitThread();
        }
    }
}
