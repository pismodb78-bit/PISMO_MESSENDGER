using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace PISMO
{
    // ДИЗАЙН формы (построение интерфейса). Вынесен из логики по образцу
    // MainForm.Designer.cs: здесь ТОЛЬКО создание/раскладка контролов.
    public partial class CallForm
    {
        // ════════════════════════════════════════════════════════════
        //  UI — изменяемый размер + zoom
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "PISMO — Звонок";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(660, 540);
            MinimumSize = new Size(480, 400);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(32, 34, 37);
            Font = new Font("Segoe UI", 9.5f);

            // enable keyboard handling for shortcuts
            KeyPreview = true;
            this.KeyDown += CallForm_KeyDown;

            // Верхняя панель
            _lblName = new Label
            {
                Text = _peerName,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(12, 10)
            };
            _lblStatus = new Label
            {
                Text = _isCaller ? "Вызов…" : "Соединение…",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(12, 34)
            };
            _lblDuration = new Label
            {
                Text = "",
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(87, 171, 90),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(600, 10)
            };
            // Плашка пинга (RTT) во время звонка.
            _lblPing = new Label
            {
                Text = "",
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(120, 220, 130),
                BackColor = Color.FromArgb(20, 21, 24),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Padding = new Padding(5, 2, 5, 2),
                Location = new Point(600, 30),
                Visible = false
            };
            _lblScreenBadge = new Label
            {
                Text = "🖥 Демонстрация",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(88, 101, 242),
                AutoSize = true,
                Location = new Point(12, 56),
                Padding = new Padding(5, 2, 5, 2),
                Visible = false
            };

            // Zoom label
            _lblZoom = new Label
            {
                Text = "🔍 100%",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(185, 187, 190),
                BackColor = Color.FromArgb(20, 21, 24, 180),
                AutoSize = true,
                Location = new Point(12, 0),   // позиция обновляется в Resize
                Visible = false,
                Padding = new Padding(4, 2, 4, 2)
            };

            // Видео удалённого
            _pbRemote = new PictureBox
            {
                BackColor = Color.FromArgb(20, 21, 24),
                SizeMode = PictureBoxSizeMode.Zoom,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(0, 56),
                Size = new Size(660, 400)
            };
            _pbRemote.Paint += PbRemote_Paint;

            // Zoom: колесо мыши
            _pbRemote.MouseWheel += (s, e) =>
            {
                _zoom += e.Delta > 0 ? 0.1f : -0.1f;
                _zoom = Math.Clamp(_zoom, 1.0f, 5.0f);
                UpdateZoomLabel();
                _pbRemote.Invalidate();
            };
            // Zoom: двойной клик — сброс
            _pbRemote.DoubleClick += (s, e) =>
            {
                _zoom = 1.0f;
                _panOffset = PointF.Empty;
                UpdateZoomLabel();
                _pbRemote.Invalidate();
            };
            // Pan: перетаскивание
            _pbRemote.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && _zoom > 1.0f)
                {
                    _panning = true;
                    _panStart = e.Location;
                    _pbRemote.Cursor = Cursors.SizeAll;
                }
            };
            _pbRemote.MouseMove += (s, e) =>
            {
                if (!_panning) return;
                _panOffset.X += e.X - _panStart.X;
                _panOffset.Y += e.Y - _panStart.Y;
                _panStart = e.Location;
                _pbRemote.Invalidate();
            };
            _pbRemote.MouseUp += (s, e) =>
            {
                _panning = false;
                _pbRemote.Cursor = Cursors.Default;
            };

            // Локальное видео (PiP)
            _pbLocal = new PictureBox
            {
                BackColor = Color.FromArgb(47, 49, 54),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                Size = new Size(120, 90),
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            // Камера собеседника — отдельная область от основной (_pbRemote),
            // которая теперь зарезервирована за демонстрацией экрана. Раньше
            // оба источника (камера и экран) писали в один _pbRemote.Image,
            // и при одновременной демонстрации экрана и включённой камере
            // кадры перезатирали друг друга в произвольном порядке, создавая
            // мерцание между картинкой экрана и лицом собеседника.
            _pbRemoteCamera = new PictureBox
            {
                BackColor = Color.FromArgb(47, 49, 54),
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle,
                Size = new Size(120, 90),
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            // Кнопки — нижняя панель
            _pnlButtons = new Panel
            {
                BackColor = Color.FromArgb(24, 25, 28),
                Height = 76,
                Dock = DockStyle.Bottom
            };

            _btnMute = MakeBtn("🎤", 0);
            _btnCamera = MakeBtn("🚫", 1);
            _btnCamera.Visible = true; // камеру можно включить в любом звонке (одна кнопка звонка)
            // Камера выключена при входе — кнопка красная, как в Discord.
            _btnCamera.BackColor = Color.FromArgb(240, 71, 71);
            _btnScreen = MakeBtn("🖥", 2);
            _btnAudio = MakeBtn("🔊", 3);
            _btnHangup = MakeBtn("📵", 4);
            _btnHangup.BackColor = Color.FromArgb(240, 71, 71);

            _btnMute.Click += (s, e) => ToggleMute();
            _btnCamera.Click += (s, e) => ToggleCamera();
            _btnScreen.Click += (s, e) => ToggleScreen();
            _btnAudio.Click += (s, e) => ToggleAudioPanel();
            _btnHangup.Click += (s, e) => EndCall();

            // Ползунок громкости звука демонстрации экрана собеседника.
            // Видим только когда собеседник реально демонстрирует экран.
            _tbScreenAudioVolume = new TrackBar
            {
                Minimum = 0,
                Maximum = 200,
                Value = 100,
                TickStyle = TickStyle.None,
                Size = new Size(140, 30),
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _tbScreenAudioVolume.ValueChanged += (s, e) =>
            {
                _remoteScreenAudioVolume = _tbScreenAudioVolume.Value / 100f;
                try { _transport?.SetRemoteScreenAudioVolume(_remoteScreenAudioVolume); } catch { }
            };
            _lblScreenAudioVolume = new Label
            {
                Text = "🔊 Звук демки",
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _pnlButtons.Controls.AddRange(new Control[] { _btnMute, _btnCamera, _btnScreen, _btnAudio, _btnHangup });

            _pnlParticipants = new Panel
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Size = new Size(180, 110),
                Location = new Point(ClientSize.Width - 190, 10),
                BackColor = Color.FromArgb(47, 49, 54), // Фирменный темно-серый цвет Discord
                BorderStyle = BorderStyle.None,
                AutoScroll = true
            };
            _lblParticipants = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Padding = new Padding(8),
                Text = "Участники:\n• Загрузка..."
            };
            _pnlParticipants.Controls.Add(_lblParticipants);

            Controls.AddRange(new Control[]
            {
                _pbRemote, _pbLocal, _pbRemoteCamera,
                _lblName, _lblStatus, _lblDuration, _lblPing,
                _lblScreenBadge, _lblZoom,
                _tbScreenAudioVolume, _lblScreenAudioVolume,
                _pnlButtons, _pnlParticipants
            });
            _pnlParticipants.BringToFront();
            _lblPing.BringToFront();
            _lblPing.Cursor = Cursors.Hand;
            _lblPing.Click += (s, e) => TogglePingGraph();

            Resize += (s, e) => LayoutControls();
            LayoutControls();

            _durationTimer.Tick += (s, e) =>
            {
                if (_connected)
                    _lblDuration.Text = (DateTime.Now - _startTime).ToString(@"mm\:ss");

                // Голосовой канал сервера: участников берём из плиток (нет call_participants).
                if (_isChannel)
                {
                    try
                    {
                        var parts = new System.Collections.Generic.List<string> { "• Вы" };
                        foreach (var n in _participants.Values) parts.Add("• " + n);
                        if (_lblParticipants != null && !_lblParticipants.IsDisposed)
                            _lblParticipants.Text = $"В канале ({parts.Count}):\n" + string.Join("\n", parts);
                    }
                    catch { }

                    // Heartbeat присутствия в канале раз в ~5 секунд (в фоне).
                    // «В эфире» = включена камера или демонстрация экрана.
                    if (_vchId > 0 && (++_vchTick % 5 == 0))
                    {
                        bool streaming = _cameraStarted || _screenSharing;
                        System.Threading.Tasks.Task.Run(() =>
                            VoicePresence.Heartbeat(_vchId, UserSession.EffectiveId, streaming));
                    }
                    return;
                }

                // Список участников и таймер 3 минут: запрос — РАЗ В 3 СЕКУНДЫ и
                // В ФОНЕ. Раньше JOIN по call_participants шёл КАЖДУЮ секунду
                // синхронно на UI-потоке — интерфейс фризило весь звонок.
                if ((++_partsTick % 3) != 0 || _partsBusy) return;
                _partsBusy = true;
                int sidParts = _sessionId;
                System.Threading.Tasks.Task.Run(() =>
                {
                    System.Collections.Generic.List<string> parts = null;
                    try
                    {
                        using var conn = DBHelper.OpenConnection();
                        // Имена участников через JOIN с users (в call_participants только user_id).
                        using var cmd = new MySqlCommand(
                            "SELECT TRIM(CONCAT(u.Name, ' ', u.Surname)) AS user_name, u.login FROM call_participants cp JOIN users u ON u.id = cp.user_id WHERE cp.call_id=@cid ORDER BY cp.joined_at ASC", conn);
                        cmd.Parameters.AddWithValue("@cid", sidParts);
                        using var r = cmd.ExecuteReader();
                        parts = new System.Collections.Generic.List<string>();
                        while (r.Read())
                        {
                            string uname = r["user_name"].ToString().Trim();
                            if (string.IsNullOrWhiteSpace(uname)) uname = r["login"].ToString();
                            parts.Add("• " + uname);
                        }
                    }
                    catch { /* запрос не удался — этот тик просто пропускаем */ }
                    finally { _partsBusy = false; }

                    if (parts == null || IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (_lblParticipants != null && !_lblParticipants.IsDisposed)
                                    _lblParticipants.Text = "Участники (" + parts.Count + "):\n" + string.Join("\n", parts);

                                // Таймер 3 минут для личных звонков (не групп) — в стиле Discord.
                                if (_groupId < 0)
                                {
                                    if (parts.Count == 1)
                                    {
                                        if (_threeMinStartTime == DateTime.MinValue) _threeMinStartTime = DateTime.Now;
                                        var elapsed = DateTime.Now - _threeMinStartTime;
                                        var remaining = TimeSpan.FromSeconds(180) - elapsed;
                                        if (remaining.TotalSeconds <= 0)
                                        {
                                            _threeMinTimerExpired = true;
                                            EndCall();
                                        }
                                        else
                                        {
                                            _lblStatus.Text = $"Ожидание собеседника... (завершится через {remaining:mm\\:ss})";
                                        }
                                    }
                                    else
                                    {
                                        _threeMinStartTime = DateTime.MinValue; // сброс: зашёл второй участник
                                        if (_connected) _lblStatus.Text = "Соединение установлено";
                                    }
                                }
                            }
                            catch { }
                        }));
                    }
                    catch { }
                });
            };
            _durationTimer.Start();
        }
    }
}
