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
    public partial class CallForm : Form
    {
        // ── Поля ────────────────────────────────────────────────────
        private readonly int _sessionId;
        private readonly bool _isCaller;
        private readonly string _peerName;
        private readonly int _peerId;
        private bool _hasVideo;
        private WebRtcTransport _transport = null;
        private System.Windows.Forms.Timer _signalTimer = null;  // ← явная инициализация
        private System.Windows.Forms.Timer _durationTimer = null;  // ← явная инициализация
        private DateTime _startTime;
        private bool _connected = false;
        private bool _ended = false;
        private bool _callLogged = false;

        // TURN кредо теперь управляются внутри CallTransport через TurnSettings.GetCredentials()

        // Аудио (микрофон + воспроизведение голоса/звука демки) полностью на
        // стороне LiveKit — NAudio для звонка больше не используется.
        // Громкость звука демонстрации экрана собеседника (0.0 - 1.0), отдельно от голоса
        private float _remoteScreenAudioVolume = 1.0f;
        private TrackBar _tbScreenAudioVolume;
        private Label _lblScreenAudioVolume;
        private bool _muted = false;

        // Камера теперь как настоящий WebRTC video track (getUserMedia внутри
        // WebRtcTransport), а не AForge VideoCaptureDevice + JPEG-over-DataChannel.
        private bool _cameraOff = false;
        private bool _cameraStarted = false;
        private bool _pendingVideoStart = false; // ждём установления соединения перед запуском камеры

        private Thread _screenThread;
        private volatile bool _screenSharing = false;
        private IntPtr _screenWindowHandle = IntPtr.Zero;
        private string _screenWindowTitle = "";
        private bool _peerScreenSharing = false;

        private float _zoom = 1.0f;
        private PointF _panOffset = PointF.Empty;
        private Point _panStart;
        private bool _panning = false;

        private PictureBox _pbRemote;
        private PictureBox _pbLocal;
        private PictureBox _pbRemoteCamera; // отдельная область для камеры собеседника, не конфликтует с экраном
        private Label _lblStatus;
        private Label _lblDuration;
        private Label _lblName;
        private Button _btnMute;
        private Button _btnCamera;
        private Button _btnScreen;
        private Button _btnAudio;
        private Button _btnHangup;

        // Громкость голоса собеседников и состояние «заглушить всех».
        private float _remoteVoiceVolume = 1.0f;
        private bool _remoteAllMuted = false;
        private Form _audioPanel;
        private Label _lblScreenBadge;
        private Label _lblZoom;
        private Panel _pnlButtons;

        private readonly int _groupId;
        private Panel _pnlParticipants;
        private Label _lblParticipants;
        private DateTime _threeMinStartTime = DateTime.MinValue;
        private bool _threeMinTimerExpired = false;

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr h, System.Text.StringBuilder t, int c);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        // ── Конструктор ─────────────────────────────────────────────
        public CallForm(int sessionId, bool isCaller, string peerName, int peerId, bool hasVideo = false, int groupId = -1)
        {
            _sessionId = sessionId;
            _isCaller = isCaller;
            _peerName = peerName;
            _peerId = peerId;
            _hasVideo = hasVideo;
            _groupId = groupId;

            // В БД таблица call_participants уже существует и имеет структуру (id, call_id, user_id, joined_at, left_at, ip, port).
            // Добавляем текущего пользователя по call_id и user_id без несуществующей колонки user_name.
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("INSERT INTO call_participants (call_id, user_id, joined_at) VALUES (@cid, @uid, NOW())", conn);
                cmd.Parameters.AddWithValue("@cid", _sessionId);
                cmd.Parameters.AddWithValue("@uid", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();
            }
            catch { /* Игнорируем возможные ошибки дублирования */ }

            // ← Инициализируем таймер ДО BuildUi (т.к. BuildUi на него ссылается)
            _durationTimer = new System.Windows.Forms.Timer { Interval = 1000 };

            BuildUi();
            StartCallSetup();
        }

        // ════════════════════════════════════════════════════════════
        //  UI — изменяемый размер + zoom
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "PISMO — Звонок";
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
            _btnCamera = MakeBtn("📷", 1);
            _btnCamera.Visible = _hasVideo;
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
                Maximum = 100,
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
                _lblName, _lblStatus, _lblDuration,
                _lblScreenBadge, _lblZoom,
                _tbScreenAudioVolume, _lblScreenAudioVolume,
                _pnlButtons, _pnlParticipants
            });
            _pnlParticipants.BringToFront();

            Resize += (s, e) => LayoutControls();
            LayoutControls();

            _durationTimer.Tick += (s, e) =>
            {
                if (_connected)
                    _lblDuration.Text = (DateTime.Now - _startTime).ToString(@"mm\:ss");

                // Проверка участников и таймера 3 минут
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    // Запрашиваем имена участников через JOIN с таблицей users, так как в call_participants хранятся только user_id
                    using var cmd = new MySqlCommand(
                        "SELECT TRIM(CONCAT(u.Name, ' ', u.Surname)) AS user_name, u.login FROM call_participants cp JOIN users u ON u.id = cp.user_id WHERE cp.call_id=@cid ORDER BY cp.joined_at ASC", conn);
                    cmd.Parameters.AddWithValue("@cid", _sessionId);
                    using var r = cmd.ExecuteReader();
                    var parts = new System.Collections.Generic.List<string>();
                    while (r.Read())
                    {
                        string uname = r["user_name"].ToString().Trim();
                        if (string.IsNullOrWhiteSpace(uname)) uname = r["login"].ToString();
                        parts.Add("• " + uname);
                    }
                    r.Close();

                    if (_lblParticipants != null && !_lblParticipants.IsDisposed)
                    {
                        _lblParticipants.Text = "Участники (" + parts.Count + "):\n" + string.Join("\n", parts);
                    }

                    // Логика таймера 3 минут для личных звонков (не групп) — в стиле Discord
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
                            _threeMinStartTime = DateTime.MinValue; // сброс таймера, если зашел второй участник
                            if (_connected) _lblStatus.Text = "Соединение установлено";
                        }
                    }
                }
                catch { }
            };
            _durationTimer.Start();
        }

        private void LayoutControls()
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int btnPanelH = _pnlButtons.Height;

            _pbRemote.Location = new Point(0, 56);
            _pbRemote.Size = new Size(w, h - 56 - btnPanelH);

            _pbLocal.Location = new Point(w - 130, 130);
            if (_pnlParticipants != null) _pnlParticipants.Location = new Point(w - 190, 10);
            _pbRemoteCamera.Location = new Point(10, _pbRemote.Bottom - 100);
            _lblDuration.Location = new Point(w - 260, 10);
            _lblZoom.Location = new Point(8, _pbRemote.Bottom - 24);

            _lblScreenAudioVolume.Location = new Point(w - 150, 70);
            _tbScreenAudioVolume.Location = new Point(w - 150, 90);

            int btnCount = 5;
            int btnW = 56, btnH = 56, gap = 12;
            int totalW = btnCount * btnW + (btnCount - 1) * gap;
            int startX = (_pnlButtons.Width - totalW) / 2;
            int btnY = (_pnlButtons.Height - btnH) / 2;
            var btns = new Button[] { _btnMute, _btnCamera, _btnScreen, _btnAudio, _btnHangup };
            for (int i = 0; i < btns.Length; i++)
                btns[i].Location = new Point(startX + i * (btnW + gap), btnY);
        }

        private void PbRemote_Paint(object sender, PaintEventArgs e)
        {
            var pb = _pbRemote;
            var img = pb.Image;

            if (img == null)
            {
                // Заглушка
                using var f = new Font("Segoe UI", 13f);
                string msg = _hasVideo ? "Ожидание видео…" : "🔊 Аудиозвонок";
                var sz = e.Graphics.MeasureString(msg, f);
                e.Graphics.DrawString(msg, f,
                    new SolidBrush(Color.FromArgb(114, 118, 125)),
                    (pb.Width - sz.Width) / 2f, (pb.Height - sz.Height) / 2f);
                return;
            }

            if (_zoom <= 1.0f && _panOffset == PointF.Empty)
            {
                // Обычный Zoom-режим (SizeMode.Zoom эмуляция)
                float scaleX = (float)pb.Width / img.Width;
                float scaleY = (float)pb.Height / img.Height;
                float scale = Math.Min(scaleX, scaleY);
                float dw = img.Width * scale;
                float dh = img.Height * scale;
                float dx = (pb.Width - dw) / 2f;
                float dy = (pb.Height - dh) / 2f;
                e.Graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                e.Graphics.DrawImage(img,
                    new RectangleF(dx, dy, dw, dh),
                    new RectangleF(0, 0, img.Width, img.Height),
                    GraphicsUnit.Pixel);
            }
            else
            {
                // Zoom + Pan
                float scaleX = (float)pb.Width / img.Width;
                float scaleY = (float)pb.Height / img.Height;
                float base_ = Math.Min(scaleX, scaleY);
                float scale = base_ * _zoom;
                float dw = img.Width * scale;
                float dh = img.Height * scale;
                float dx = (pb.Width - dw) / 2f + _panOffset.X;
                float dy = (pb.Height - dh) / 2f + _panOffset.Y;
                e.Graphics.InterpolationMode =
                    System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                e.Graphics.DrawImage(img,
                    new RectangleF(dx, dy, dw, dh),
                    new RectangleF(0, 0, img.Width, img.Height),
                    GraphicsUnit.Pixel);
            }
        }

        private void UpdateZoomLabel()
        {
            _lblZoom.Text = $"🔍 {(int)(_zoom * 100)}%";
            _lblZoom.Visible = _zoom > 1.0f;
        }

        private Button MakeBtn(string emoji, int idx)
        {
            var b = new Button
            {
                Text = emoji,
                Font = new Font("Segoe UI", 16f),
                Size = new Size(56, 56),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        // ════════════════════════════════════════════════════════════
        //  СИГНАЛИНГ
        // ════════════════════════════════════════════════════════════
        /// <summary>Имя комнаты LiveKit — общее для всех участников одного звонка.</summary>
        private string RoomName => "call_" + _sessionId;

        private async void StartCallSetup()
        {
            // Плиточная сетка участников (Discord-style).
            BuildTilesHost();

            _transport = new WebRtcTransport();
            _transport.Disconnected += OnPeerDisconnected;
            _transport.Connected += OnConnected;
            // В личном звонке уход единственного собеседника = конец звонка.
            // В групповом — остальные продолжают, поэтому событие игнорируем.
            _transport.RemoteParticipantLeft += () =>
            {
                if (_groupId < 0) UiInvoke(OnPeerDisconnected);
            };

            // --- Плитки участников (камера/экран каждого собеседника) ---
            _transport.ParticipantJoined += (pid, name) => UiInvoke(() => AddParticipant(pid, name));
            _transport.ParticipantLeftById += pid => UiInvoke(() => RemoveParticipant(pid));
            _transport.RemoteTileStarted += (pid, name, source) => UiInvoke(() => OnTileStarted(pid, name, source));
            _transport.RemoteTileStopped += (pid, source) => UiInvoke(() => OnTileStopped(pid, source));
            _transport.RemoteTileFrame += (pid, source, frame) => UiInvoke(() => OnTileFrame(pid, source, frame));
            _transport.ActiveSpeakers += json => UiInvoke(() => OnActiveSpeakers(json));

            // --- Демонстрация экрана (своя) ---
            _transport.LocalScreenStarted += () => UiInvoke(OnLocalScreenStarted);
            _transport.LocalScreenStopped += () => UiInvoke(OnLocalScreenStopped);
            _transport.LocalScreenError += err => UiInvoke(() => OnLocalScreenError(err));
            _transport.ScreenPreviewFrameReceived += frame => UiInvoke(() => ShowScreenSharePip(frame));
            _transport.ScreenPreviewReady += () => UiInvoke(() =>
            {
                _transport.HideTransportWindow();
                _screenPreviewPending = false;
                _screenSharing = true;
                _btnScreen.BackColor = Color.FromArgb(88, 101, 242);
                _btnScreen.Text = "⏹";
                _transport.ConfirmScreenShare();
                ShowScreenSharePipContainer();
            });

            // --- Своя камера: кадры идут в собственную плитку ---
            _transport.LocalCameraFrameReceived += frame => UiInvoke(() => OnSelfCameraFrame(frame));
            _transport.CameraPreviewReady += () => UiInvoke(() => _cameraPreviewForm?.SetConfirmEnabled(true));
            _transport.LocalCameraStarted += () => UiInvoke(OnLocalCameraStarted);
            _transport.LocalCameraStopped += () => UiInvoke(() => { OnLocalCameraStopped(); OnSelfCameraStopped(); });
            _transport.LocalCameraError += err => UiInvoke(() => OnLocalCameraError(err));

            // --- LiveKit: подключение к комнате ---
            // Сигналинг, ICE/TURN, renegotiation и многосторонность берёт на себя
            // LiveKit-сервер. От нас нужно только сгенерировать access-токен и
            // подключиться к общей комнате (имя = id call-сессии).
            try
            {
                string identity = UserSession.EffectiveId.ToString();
                string displayName = string.IsNullOrWhiteSpace(UserSession.EffectiveName)
                    ? identity : UserSession.EffectiveName;
                string token = LiveKitSettings.CreateToken(RoomName, identity, displayName);

                UiInvoke(() => _lblStatus.Text = "Подключение к серверу…");

                // Статус сессии (ringing → active → ended/rejected) ведёт общий
                // флоу: INSERT создаёт 'ringing', приём звонка ставит 'active'.
                // CallForm его не трогает — иначе звонящий, мгновенно подключаясь
                // к комнате LiveKit, преждевременно переводил бы сессию в 'active'
                // и у вызываемого не появлялся бы экран входящего звонка.
                await _transport.InitAsync(this, LiveKitSettings.Url, token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LIVEKIT SETUP ERROR] {ex.Message}");
                UiInvoke(() => _lblStatus.Text = $"Ошибка: {ex.Message}");
            }

            WebSocketSignalingClient.Instance.OnMessageReceived += OnWebSocketMessage;

            // Камеру стартуем только после установления соединения (OnConnected),
            // чтобы превью открывалось, когда комната уже готова принять трек.
            if (_hasVideo)
                _pendingVideoStart = true;

            // Постоянный опрос статуса — для корректного закрытия формы при
            // отклонении/завершении звонка собеседником.
            _signalTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _signalTimer.Tick += (s, e) => PollCallStatus();
            _signalTimer.Start();
        }

        private void OnWebSocketMessage(string type, int senderId, int sessionId, string payload)
        {
            if (sessionId != _sessionId || IsDisposed) return;
            UiInvoke(() =>
            {
                if (type == "call_status" || type == "incoming_call")
                    PollCallStatus();
            });
        }

        private void UiInvoke(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>Опрос статуса call-сессии. С LiveKit это нужно только для
        /// корректного закрытия формы, когда собеседник отклонил или завершил
        /// звонок — само медиа-соединение поднимает LiveKit-сервер.</summary>
        private void PollCallStatus()
        {
            if (_ended) return;

            try
            {
                string status = null;
                using (var conn = DBHelper.OpenConnection())
                using (var cmd = new MySqlCommand(
                    "SELECT status FROM call_sessions WHERE id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", _sessionId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                        status = reader["status"]?.ToString();
                }

                if (status == "rejected" || status == "ended")
                {
                    _lblStatus.Text = status == "rejected" ? "Звонок отклонён" : "Завершён";
                    if (!_ended) MarkCallEnded();
                    _ended = true;
                    _signalTimer?.Stop();

                    var t = new System.Windows.Forms.Timer { Interval = 1200 };
                    t.Tick += (_, __) => { t.Stop(); if (!IsDisposed) Close(); };
                    t.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CALL POLL ERROR] {ex.Message}");
            }
        }

        private void OnConnected()
        {
            if (_connected) return;
            _connected = true;
            _startTime = DateTime.Now;
            _lblStatus.Text = "Соединение установлено";
            _durationTimer.Start();

            // Камеру запускаем только после подключения к комнате — превью
            // открывается, когда транспорт уже готов опубликовать трек.
            if (_pendingVideoStart)
            {
                _pendingVideoStart = false;
                StartVideo();
            }
        }

        private void OnPeerDisconnected()
        {
            if (_ended) return;
            _lblStatus.Text = "Соединение разорвано";

            MarkCallEnded();

            _ended = true;
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, __) => { t.Stop(); if (!IsDisposed) Close(); };
            t.Start();
        }

        // ════════════════════════════════════════════════════════════
        //  ДЕМОНСТРАЦИЯ ЭКРАНА — настоящий WebRTC video track
        // ════════════════════════════════════════════════════════════

        /// <summary>Декодированный кадр удалённого видео-трека демонстрации экрана.
        /// Источник кадра другой (canvas.toBlob внутри WebView2, не DataChannel-пакет),
        /// но путь отображения тот же, что и для старого JPEG-pipeline.</summary>
        private void ShowRemoteScreenFrame(byte[] jpegBytes)
        {
            ShowRemoteImage(jpegBytes, isScreen: true);
        }

        private void OnRemoteScreenStarted()
        {
            _peerScreenSharing = true;
            _tbScreenAudioVolume.Visible = true;
            _lblScreenAudioVolume.Visible = true;
            _lblScreenBadge.Text = "🖥 Собеседник показывает экран";
            _lblScreenBadge.Visible = true;
            // Экран занимает основную область — камера (если есть) уходит в PiP.
            if (_remoteCameraActive) _pbRemoteCamera.Visible = true;
        }

        private void OnRemoteScreenStopped()
        {
            _peerScreenSharing = false;
            _tbScreenAudioVolume.Visible = false;
            _lblScreenAudioVolume.Visible = false;
            if (_lblScreenBadge.Visible && _lblScreenBadge.Text.Contains("Собеседник"))
                _lblScreenBadge.Visible = false;

            // Экран больше не идёт — чистим основную область от последнего кадра
            // экрана. Если камера активна, она снова начнёт рисоваться в основной
            // области (и PiP прячем), иначе вернётся «Ожидание видео».
            var old = _pbRemote.Image;
            _pbRemote.Image = null;
            old?.Dispose();
            _pbRemote.Invalidate();
            if (_remoteCameraActive)
            {
                _pbRemoteCamera.Visible = false;
                var oldC = _pbRemoteCamera.Image;
                _pbRemoteCamera.Image = null;
                oldC?.Dispose();
            }
        }

        private void OnLocalScreenStarted()
        {
            _lblScreenBadge.Text = "🖥 Демонстрация экрана";
            _lblScreenBadge.Visible = true;
        }

        private void OnLocalScreenStopped()
        {
            _screenSharing = false;
            _btnScreen.BackColor = Color.FromArgb(64, 68, 75);
            _btnScreen.Text = "🖥";
            _lblScreenBadge.Visible = false;
        }

        private void OnLocalScreenError(string error)
        {
            _transport.HideTransportWindow();
            // Пользователь отменил системный диалог выбора экрана, либо доступ
            // запрещён политикой ОС — откатываем UI обратно.
            System.Diagnostics.Debug.WriteLine($"[SCREEN TRACK ERROR] {error}");
            bool isUserCancel = error != null && (error.Contains("NotAllowedError") || error == "user_cancelled_picker_window");
            if (!isUserCancel)
                _lblStatus.Text = $"Демонстрация экрана: {error}";

            _screenPreviewPending = false;
            _screenSharing = false;
            _btnScreen.BackColor = Color.FromArgb(64, 68, 75);
            _btnScreen.Text = "🖥";
            _lblScreenBadge.Visible = false;
            HideScreenSharePip();
        }

        // ════════════════════════════════════════════════════════════
        //  КАМЕРА — обработчики событий video track
        // ════════════════════════════════════════════════════════════
        private bool _remoteCameraActive = false;

        private void OnRemoteCameraStarted()
        {
            _remoteCameraActive = true;
            // Если собеседник одновременно демонстрирует экран — камера идёт в
            // маленький PiP-уголок (главную область занимает экран). Иначе
            // камера показывается в основной области (_pbRemote), чтобы не
            // оставалось «Ожидание видео».
            _pbRemoteCamera.Visible = _peerScreenSharing;
        }

        private void OnRemoteCameraStopped()
        {
            _remoteCameraActive = false;
            _pbRemoteCamera.Visible = false;
            var oldC = _pbRemoteCamera.Image;
            _pbRemoteCamera.Image = null;
            oldC?.Dispose();

            // Если камера показывалась в основной области и экран не
            // демонстрируется — очищаем основную область (вернётся «Ожидание видео»).
            if (!_peerScreenSharing)
            {
                var oldR = _pbRemote.Image;
                _pbRemote.Image = null;
                oldR?.Dispose();
                _pbRemote.Invalidate();
            }
        }

        private void OnLocalCameraStarted()
        {
            // _pbLocal.Visible уже установлен в StartVideo(); ничего
            // дополнительного делать не требуется — кадры начнут приходить
            // через LocalCameraFrameReceived.
        }

        private void OnLocalCameraStopped()
        {
            _cameraStarted = false;
            var old = _pbLocal.Image;
            _pbLocal.Image = null;
            old?.Dispose();
        }

        private void OnLocalCameraError(string error)
        {
            // Пользователь не дал доступ к камере, либо устройство занято
            // другим приложением — откатываем UI обратно, закрываем превью.
            System.Diagnostics.Debug.WriteLine($"[CAMERA TRACK ERROR] {error}");
            _cameraStarted = false;
            _cameraOff = true;
            _btnCamera.Text = "🚫";
            _btnCamera.BackColor = Color.FromArgb(240, 71, 71);
            _lblStatus.Text = "Камера недоступна";

            if (_cameraPreviewForm != null)
            {
                var form = _cameraPreviewForm;
                _cameraPreviewForm = null;
                try { form.Close(); } catch { }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  АУДИО — полностью на стороне LiveKit (публикация микрофона и
        //  воспроизведение голоса/звука демки делает сам SDK). Здесь остаётся
        //  только переключение mute.
        // ════════════════════════════════════════════════════════════
        private void ToggleMute()
        {
            _muted = !_muted;
            _btnMute.Text = _muted ? "🔇" : "🎤";
            _btnMute.BackColor = _muted
                ? Color.FromArgb(240, 71, 71) : Color.FromArgb(64, 68, 75);
            try { _transport?.SetMicrophoneEnabled(!_muted); } catch { }
        }

        // ── Панель управления входящим звуком: громкость голоса собеседников,
        //    громкость демонстрации экрана и тумблер «заглушить всех». ──
        private void ToggleAudioPanel()
        {
            if (_audioPanel != null && !_audioPanel.IsDisposed)
            {
                try { _audioPanel.Close(); } catch { }
                _audioPanel = null;
                return;
            }

            _audioPanel = new Form
            {
                Text = "Звук и устройства",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(40, 42, 46),
                ClientSize = new Size(310, 470)
            };
            var anchor = PointToScreen(new Point(_pnlButtons.Left, _pnlButtons.Top));
            _audioPanel.Location = new Point(
                Math.Max(0, anchor.X + (_pnlButtons.Width - 310) / 2),
                Math.Max(0, anchor.Y - 480));

            int y = 12;
            Label MkLbl(string t)
            {
                var l = new Label { Text = t, ForeColor = Color.FromArgb(220, 221, 222), AutoSize = true, Location = new Point(14, y), Font = new Font("Segoe UI", 9f) };
                _audioPanel.Controls.Add(l); y += 20; return l;
            }
            ComboBox MkCombo()
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, y), Size = new Size(282, 24), FlatStyle = FlatStyle.Flat };
                _audioPanel.Controls.Add(cb); y += 34; return cb;
            }
            TrackBar MkTb(int val)
            {
                var tb = new TrackBar { Minimum = 0, Maximum = 200, Value = Math.Min(200, val), TickStyle = TickStyle.None, Location = new Point(8, y), Size = new Size(290, 40) };
                _audioPanel.Controls.Add(tb); y += 46; return tb;
            }

            MkLbl("🎤 Микрофон");
            var cmbMic = MkCombo();
            MkLbl("🔊 Устройство вывода");
            var cmbSpk = MkCombo();
            MkLbl("📷 Камера");
            var cmbCam = MkCombo();

            MkLbl("🖥 Качество демонстрации");
            var rowQ = y;
            var cmbRes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, rowQ), Size = new Size(135, 24), FlatStyle = FlatStyle.Flat };
            var cmbFps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(161, rowQ), Size = new Size(135, 24), FlatStyle = FlatStyle.Flat };
            cmbRes.Items.AddRange(new object[] { "1080p", "720p", "480p", "360p" });
            cmbFps.Items.AddRange(new object[] { "60 fps", "30 fps", "15 fps" });
            cmbRes.SelectedItem = DeviceSettings.ScreenShareResolutionHeight + "p";
            if (cmbRes.SelectedIndex < 0) cmbRes.SelectedIndex = 0;
            cmbFps.SelectedItem = DeviceSettings.ScreenShareFps + " fps";
            if (cmbFps.SelectedIndex < 0) cmbFps.SelectedIndex = 1;
            cmbRes.SelectedIndexChanged += (s, e) =>
            {
                int h = int.Parse(((string)cmbRes.SelectedItem).Replace("p", ""));
                DeviceSettings.ScreenShareResolutionHeight = h; try { DeviceSettings.Save(); } catch { }
            };
            cmbFps.SelectedIndexChanged += (s, e) =>
            {
                int f = int.Parse(((string)cmbFps.SelectedItem).Replace(" fps", ""));
                DeviceSettings.ScreenShareFps = f; try { DeviceSettings.Save(); } catch { }
            };
            _audioPanel.Controls.Add(cmbRes);
            _audioPanel.Controls.Add(cmbFps);
            y = rowQ + 34;
            var lblHint = new Label { Text = "(применится при следующем запуске демонстрации)", ForeColor = Color.FromArgb(140, 142, 148), AutoSize = true, Location = new Point(14, y), Font = new Font("Segoe UI", 7.5f) };
            _audioPanel.Controls.Add(lblHint); y += 24;

            MkLbl("Громкость собеседников");
            var tbVoice = MkTb((int)(_remoteVoiceVolume * 100));
            tbVoice.ValueChanged += (s, e) => { _remoteVoiceVolume = tbVoice.Value / 100f; try { _transport?.SetRemoteVoiceVolume(_remoteVoiceVolume); } catch { } };

            MkLbl("Громкость демонстрации");
            var tbScreen = MkTb((int)(_remoteScreenAudioVolume * 100));
            tbScreen.ValueChanged += (s, e) => { _remoteScreenAudioVolume = tbScreen.Value / 100f; try { _transport?.SetRemoteScreenAudioVolume(_remoteScreenAudioVolume); } catch { } };

            var chkMute = new CheckBox { Text = "🔇 Заглушить весь звук", ForeColor = Color.FromArgb(220, 221, 222), AutoSize = true, Location = new Point(14, y), Checked = _remoteAllMuted, Font = new Font("Segoe UI", 9.5f) };
            chkMute.CheckedChanged += (s, e) => { _remoteAllMuted = chkMute.Checked; try { _transport?.SetRemoteMuted(_remoteAllMuted); } catch { } };
            _audioPanel.Controls.Add(chkMute);

            // Заполняем списки устройств из браузера.
            void OnDevices(string camsJson, string micsJson, string spkJson)
            {
                try
                {
                    var cams = JsonSerializer.Deserialize<string[]>(camsJson) ?? Array.Empty<string>();
                    var mics = JsonSerializer.Deserialize<string[]>(micsJson) ?? Array.Empty<string>();
                    var spk = JsonSerializer.Deserialize<string[]>(spkJson) ?? Array.Empty<string>();
                    UiInvoke(() =>
                    {
                        if (_audioPanel == null || _audioPanel.IsDisposed) return;
                        cmbMic.Items.Clear(); cmbMic.Items.AddRange(mics);
                        cmbSpk.Items.Clear(); cmbSpk.Items.AddRange(spk);
                        cmbCam.Items.Clear(); cmbCam.Items.AddRange(cams);
                        if (!string.IsNullOrEmpty(DeviceSettings.MicrophoneName)) cmbMic.SelectedItem = DeviceSettings.MicrophoneName;
                        if (cmbMic.SelectedIndex < 0 && cmbMic.Items.Count > 0) cmbMic.SelectedIndex = 0;
                        if (cmbSpk.Items.Count > 0) cmbSpk.SelectedIndex = 0;
                        if (!string.IsNullOrEmpty(DeviceSettings.CameraName)) cmbCam.SelectedItem = DeviceSettings.CameraName;
                        if (cmbCam.SelectedIndex < 0 && cmbCam.Items.Count > 0) cmbCam.SelectedIndex = 0;
                    });
                }
                catch { }
            }
            _transport.DevicesEnumerated += OnDevices;
            cmbMic.SelectedIndexChanged += (s, e) => { if (cmbMic.SelectedItem is string m) { DeviceSettings.MicrophoneName = m; try { DeviceSettings.Save(); } catch { } _transport?.SetInputDevice(m); } };
            cmbSpk.SelectedIndexChanged += (s, e) => { if (cmbSpk.SelectedItem is string sp) _transport?.SetOutputDevice(sp); };
            cmbCam.SelectedIndexChanged += (s, e) => { if (cmbCam.SelectedItem is string cm) { DeviceSettings.CameraName = cm; try { DeviceSettings.Save(); } catch { } _transport?.SwitchCameraDevice(cm); } };

            _audioPanel.FormClosed += (s, e) => { try { _transport.DevicesEnumerated -= OnDevices; } catch { } _audioPanel = null; };
            _audioPanel.Show(this);
            _transport.EnumerateDevices();
        }

        // ════════════════════════════════════════════════════════════
        //  ВИДЕО (камера) — настоящий WebRTC video track, как и демонстрация экрана
        // ════════════════════════════════════════════════════════════
        private MediaPreviewForm _cameraPreviewForm;
        private bool _screenPreviewPending = false;
        private Form _screenPipForm; // маленькое окно "что видит собеседник" во время своей демки

        private void StartVideo()
        {
            if (_cameraStarted || _cameraPreviewForm != null) return; // уже запущено или превью открыто

            _cameraPreviewForm = new MediaPreviewForm("Включить камеру", showDevicePicker: true);
            _cameraPreviewForm.SetConfirmEnabled(false); // включится после cameraPreviewReady

            // Запрашиваем список устройств у браузера, чтобы показать актуальный
            // выбор прямо в превью-окне (можно сменить камеру без выхода из звонка).
            _transport.EnumerateDevices();
            void OnDevicesForPreview(string camsJson, string micsJson, string speakersJson)
            {
                try
                {
                    var cams = JsonSerializer.Deserialize<string[]>(camsJson) ?? Array.Empty<string>();
                    UiInvoke(() => _cameraPreviewForm?.SetDeviceList(cams, DeviceSettings.CameraName));
                }
                catch { }
            }
            _transport.DevicesEnumerated += OnDevicesForPreview;

            _cameraPreviewForm.DeviceChanged += deviceLabel =>
            {
                _transport.SwitchCameraDevice(deviceLabel);
            };
            _cameraPreviewForm.Confirmed += () =>
            {
                _transport.DevicesEnumerated -= OnDevicesForPreview;
                _cameraPreviewForm = null;
                _cameraStarted = true;
                _cameraOff = false;
                _pbLocal.Visible = true;
                _transport.ConfirmCameraShare();
            };
            _cameraPreviewForm.Cancelled += () =>
            {
                _transport.DevicesEnumerated -= OnDevicesForPreview;
                _cameraPreviewForm = null;
                _transport.CancelCameraPreview();
                // Кнопка остаётся в состоянии "выключено", раз пользователь отменил.
                _cameraOff = true;
                _btnCamera.Text = "📷";
                _btnCamera.BackColor = Color.FromArgb(64, 68, 75);
            };

            // DeviceSettings.CameraName хранит имя устройства, выбранное ДО звонка —
            // используем как стартовое для превью; пользователь может сменить его
            // прямо в окне превью через SwitchCameraDevice.
            _transport.PreviewCamera(DeviceSettings.CameraName);
            _cameraPreviewForm.Show(this);
        }

        /// <summary>Кадр своей камеры. Источник — canvas.toBlob внутри WebView2,
        /// читающий уже декодированный видео-трек. Идёт либо в превью-форму
        /// (до подтверждения), либо в _pbLocal (после подтверждения) —
        /// один и тот же поток кадров используется на обеих стадиях.</summary>
        private void ShowLocalCameraFrame(byte[] jpegBytes)
        {
            if (_cameraPreviewForm != null)
            {
                _cameraPreviewForm.UpdateFrame(jpegBytes);
                return;
            }

            if (_cameraOff) return;
            Bitmap img = null;
            try
            {
                using var ms = new MemoryStream(jpegBytes);
                img = new Bitmap(ms);
            }
            catch { img?.Dispose(); return; }

            if (!IsDisposed && _pbLocal.IsHandleCreated)
            {
                try
                {
                    _pbLocal.BeginInvoke(() =>
                    {
                        if (_pbLocal.IsDisposed) { img.Dispose(); return; }
                        var old = _pbLocal.Image;
                        _pbLocal.Image = img;
                        old?.Dispose();
                    });
                }
                catch { img.Dispose(); }
            }
            else img.Dispose();
        }

        /// <summary>Кадр камеры собеседника. Если собеседник одновременно
        /// демонстрирует экран — камера идёт в маленький PiP-уголок
        /// (_pbRemoteCamera), а экран занимает основную область. Если экрана
        /// нет — камера показывается прямо в основной области (_pbRemote),
        /// чтобы не висело «Ожидание видео».</summary>
        private void ShowRemoteCameraFrame(byte[] jpegBytes)
        {
            Bitmap img = null;
            try
            {
                using var ms = new MemoryStream(jpegBytes);
                img = new Bitmap(ms);
            }
            catch { img?.Dispose(); return; }

            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        if (IsDisposed) { img.Dispose(); return; }
                        if (_peerScreenSharing)
                        {
                            _pbRemoteCamera.Visible = true;
                            var old = _pbRemoteCamera.Image;
                            _pbRemoteCamera.Image = img;
                            old?.Dispose();
                        }
                        else
                        {
                            var old = _pbRemote.Image;
                            _pbRemote.Image = img;
                            old?.Dispose();
                            _pbRemote.Invalidate();
                        }
                    });
                }
                catch (ObjectDisposedException) { img.Dispose(); }
            }
            else img.Dispose();
        }

        private void ToggleCamera()
        {
            if (!_connected)
            {
                _lblStatus.Text = "Дождитесь соединения перед включением камеры...";
                return;
            }

            if (!_cameraOff)
            {
                // Выключение — без превью, сразу останавливаем.
                _cameraOff = true;
                _btnCamera.Text = "🚫";
                _btnCamera.BackColor = Color.FromArgb(240, 71, 71);
                _transport.StopCameraTrack();
                _cameraStarted = false;
                var old = _pbLocal.Image; _pbLocal.Image = null; old?.Dispose();
            }
            else
            {
                _btnCamera.Text = "📷";
                _btnCamera.BackColor = Color.FromArgb(64, 68, 75);
                StartVideo(); // откроет превью с подтверждением
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ДЕМОНСТРАЦИЯ ЭКРАНА — настоящий WebRTC video track (H.264,
        //  аппаратное кодирование/декодирование через GPU силами Chromium),
        //  а не самописный JPEG-pipeline через DataChannel.
        // ════════════════════════════════════════════════════════════
        private void ToggleScreen()
        {
            if (!_connected)
            {
                _lblStatus.Text = "Дождитесь соединения перед включением демки...";
                return;
            }

            if (_screenSharing)
            {
                StopScreenShare();
            }
            else if (!_screenPreviewPending)
            {
                // Системный диалог Windows ("Выберите, чем поделиться") уже
                // сам показывает превью миниатюр окон/экрана — отдельное
                // окно-подтверждение поверх него было избыточным и выглядело
                // как два конфликтующих диалога. Просто запускаем захват;
                // после того, как пользователь выбрал источник в системном
                // диалоге, демка включается автоматически.
                _screenPreviewPending = true;
                _transport.PreviewScreen(DeviceSettings.ScreenShareResolutionHeight, DeviceSettings.ScreenShareFps);
            }
        }

        private void StopScreenShare()
        {
            _screenSharing = false;
            _screenPreviewPending = false;

            // Звук демонстрации публикуется/останавливается вместе с видео-треком
            // средствами LiveKit (createLocalScreenTracks({audio:true})) — отдельный
            // WASAPI-loopback больше не нужен.
            _transport.StopScreenShareTrack();

            _btnScreen.BackColor = Color.FromArgb(64, 68, 75);
            _btnScreen.Text = "🖥";
            _lblScreenBadge.Visible = false;
            HideScreenSharePip();
        }

        // ════════════════════════════════════════════════════════════
        //  PIP-виджет своей демонстрации экрана — маленькое окно "что
        //  видит собеседник", можно свернуть в полоску и развернуть
        //  обратно, не закрывая саму демонстрацию.
        // ════════════════════════════════════════════════════════════
        private PictureBox _screenPipPicture;
        private Panel _screenPipTitleBar;
        private bool _screenPipCollapsed = false;
        private Size _screenPipExpandedSize = new Size(260, 170);
        private NotifyIcon _screenPipTrayIcon;

        private void ShowScreenSharePipContainer()
        {
            if (_screenPipForm != null) return;

            _screenPipForm = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(20, 21, 23),
                Size = _screenPipExpandedSize,
                Location = new Point(
                    Screen.PrimaryScreen.WorkingArea.Right - _screenPipExpandedSize.Width - 24,
                    Screen.PrimaryScreen.WorkingArea.Bottom - _screenPipExpandedSize.Height - 24)
            };

            _screenPipTitleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 22,
                BackColor = Color.FromArgb(40, 42, 46)
            };
            var lbl = new Label
            {
                Text = "🖥 Ваша демонстрация",
                ForeColor = Color.White,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Font = new Font("Segoe UI", 8)
            };
            var btnTray = new Button
            {
                Text = "▾",
                Dock = DockStyle.Right,
                Width = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White
            };
            btnTray.FlatAppearance.BorderSize = 0;
            btnTray.Click += (s, e) => MinimizeScreenSharePipToTray();

            var btnToggle = new Button
            {
                Text = "—",
                Dock = DockStyle.Right,
                Width = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White
            };
            btnToggle.FlatAppearance.BorderSize = 0;
            btnToggle.Click += (s, e) => ToggleScreenSharePipCollapsed();
            _screenPipTitleBar.Controls.Add(lbl);
            _screenPipTitleBar.Controls.Add(btnToggle);
            _screenPipTitleBar.Controls.Add(btnTray);

            _screenPipPicture = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 21, 23),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            _screenPipForm.Controls.Add(_screenPipPicture);
            _screenPipForm.Controls.Add(_screenPipTitleBar);

            // Перетаскивание окна за титульную полосу, метку и само превью.
            Point dragStart = Point.Empty;
            bool dragging = false;
            MouseEventHandler onMouseDown = (s, e) => { dragging = true; dragStart = e.Location; };
            MouseEventHandler onMouseMove = (s, e) =>
            {
                if (dragging)
                {
                    // Вычисляем перемещение в координатах экрана для точности
                    _screenPipForm.Location = new Point(
                        _screenPipForm.Left + e.X - dragStart.X,
                        _screenPipForm.Top + e.Y - dragStart.Y);
                }
            };
            MouseEventHandler onMouseUp = (s, e) => dragging = false;

            _screenPipTitleBar.MouseDown += onMouseDown;
            _screenPipTitleBar.MouseMove += onMouseMove;
            _screenPipTitleBar.MouseUp += onMouseUp;

            lbl.MouseDown += onMouseDown;
            lbl.MouseMove += onMouseMove;
            lbl.MouseUp += onMouseUp;

            _screenPipPicture.MouseDown += onMouseDown;
            _screenPipPicture.MouseMove += onMouseMove;
            _screenPipPicture.MouseUp += onMouseUp;

            // Уголок для изменения размеров окна (ресайз)
            var resizeHandle = new Label
            {
                Text = "◢",
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(120, 122, 127),
                BackColor = Color.FromArgb(20, 21, 23),
                Size = new Size(16, 16),
                Cursor = Cursors.SizeNWSE,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(_screenPipForm.Width - 16, _screenPipForm.Height - 16)
            };

            bool resizing = false;
            Point resizeStart = Point.Empty;
            Size startSize = Size.Empty;

            resizeHandle.MouseDown += (s, e) => { resizing = true; resizeStart = Control.MousePosition; startSize = _screenPipForm.Size; };
            resizeHandle.MouseMove += (s, e) =>
            {
                if (resizing)
                {
                    var mousePos = Control.MousePosition;
                    int newW = Math.Max(180, startSize.Width + (mousePos.X - resizeStart.X));
                    int newH = Math.Max(120, startSize.Height + (mousePos.Y - resizeStart.Y));
                    _screenPipForm.Size = new Size(newW, newH);
                    _screenPipExpandedSize = _screenPipForm.Size;
                    resizeHandle.Location = new Point(_screenPipForm.Width - 16, _screenPipForm.Height - 16);
                }
            };
            resizeHandle.MouseUp += (s, e) => resizing = false;

            _screenPipForm.Controls.Add(resizeHandle);
            resizeHandle.BringToFront();

            _screenPipForm.Show();
        }

        private void ToggleScreenSharePipCollapsed()
        {
            if (_screenPipForm == null) return;
            _screenPipCollapsed = !_screenPipCollapsed;
            if (_screenPipCollapsed)
            {
                _screenPipExpandedSize = _screenPipForm.Size;
                _screenPipPicture.Visible = false;
                _screenPipForm.Size = new Size(180, 22);
            }
            else
            {
                _screenPipPicture.Visible = true;
                _screenPipForm.Size = _screenPipExpandedSize;
            }
        }

        /// <summary>Полностью убирает PIP-окно с экрана в системный трей.
        /// Демонстрация при этом продолжается — это только скрытие
        /// собственного превью, не остановка демки. Восстанавливается
        /// кликом по иконке в трее.</summary>
        private void MinimizeScreenSharePipToTray()
        {
            if (_screenPipForm == null) return;
            _screenPipForm.Hide();

            if (_screenPipTrayIcon == null)
            {
                _screenPipTrayIcon = new NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Text = "PISMO — Ваша демонстрация (нажмите, чтобы показать)",
                    Visible = true
                };
                _screenPipTrayIcon.Click += (s, e) => RestoreScreenSharePipFromTray();

                var menu = new ContextMenuStrip();
                menu.Items.Add("Показать превью", null, (s, e) => RestoreScreenSharePipFromTray());
                _screenPipTrayIcon.ContextMenuStrip = menu;
            }
            else
            {
                _screenPipTrayIcon.Visible = true;
            }
        }

        private void RestoreScreenSharePipFromTray()
        {
            if (_screenPipTrayIcon != null)
                _screenPipTrayIcon.Visible = false;
            _screenPipForm?.Show();
        }

        private void ShowScreenSharePip(byte[] jpegBytes)
        {
            if (_screenPipForm == null || _screenPipPicture == null || _screenPipPicture.IsDisposed) return;
            Bitmap img;
            try
            {
                using var ms = new MemoryStream(jpegBytes);
                img = new Bitmap(ms);
            }
            catch { return; }

            var old = _screenPipPicture.Image;
            _screenPipPicture.Image = img;
            old?.Dispose();
        }

        private void HideScreenSharePip()
        {
            if (_screenPipTrayIcon != null)
            {
                try { _screenPipTrayIcon.Visible = false; _screenPipTrayIcon.Dispose(); } catch { }
                _screenPipTrayIcon = null;
            }
            if (_screenPipForm == null) return;
            try { _screenPipForm.Close(); } catch { }
            _screenPipForm = null;
            _screenPipPicture = null;
            _screenPipTitleBar = null;
        }

        /// <summary>Масштабирует изображение по высоте до targetHeight; не повышает разрешение (нет апскейла).</summary>
        private static Bitmap ScaleToHeight(Bitmap src, int targetHeight)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (targetHeight <= 0) return ScaleDown(src, 960); // fallback к старой логике

            if (src.Height <= targetHeight) return (Bitmap)src.Clone();

            int newW = Math.Max(1, (int)Math.Round(src.Width * (targetHeight / (double)src.Height)));
            var dst = new Bitmap(newW, targetHeight);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, newW, targetHeight);
            return dst;
        }

        private static Bitmap ScaleDown(Bitmap src, int maxW)
        {
            if (src.Width <= maxW) return src;
            int h = (int)(src.Height * ((double)maxW / src.Width));
            var dst = new Bitmap(maxW, Math.Max(1, h));
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.DrawImage(src, 0, 0, maxW, h);
            return dst;
        }

        private static List<(IntPtr, string)> GetOpenWindows()
        {
            var list = new List<(IntPtr, string)>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string t = sb.ToString().Trim();
                if (t.Length > 2 && t != "Program Manager")
                    list.Add((hwnd, t.Length > 50 ? t[..50] + "…" : t));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ════════════════════════════════════════════════════════════
        //  ПРИЁМ ДАННЫХ — аудио теперь воспроизводит LiveKit напрямую,
        //  видео-кадры приходят отдельными событиями transport.
        // ════════════════════════════════════════════════════════════
        private void ShowRemoteImage(byte[] payload, bool isScreen)
        {
            Bitmap img = null;
            try
            {
                // Декодируем JPEG в фоне (уже в потоке транспорта, не UI)
                using var ms = new MemoryStream(payload);
                img = new Bitmap(ms); // Bitmap(Stream) копирует данные
            }
            catch { img?.Dispose(); return; }

            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(() =>
                    {
                        if (IsDisposed) { img.Dispose(); return; }
                        var old = _pbRemote.Image;
                        _pbRemote.Image = img;
                        _pbRemote.Invalidate(); // перерисовываем с zoom
                        old?.Dispose();

                        if (isScreen && !_lblScreenBadge.Visible)
                        {
                            _lblScreenBadge.Text = "🖥 Собеседник показывает экран";
                            _lblScreenBadge.Visible = true;
                        }
                        else if (!isScreen && _lblScreenBadge.Visible
                                 && _lblScreenBadge.Text.Contains("Собеседник"))
                        {
                            _lblScreenBadge.Visible = false;
                        }
                    });
            }
            catch (ObjectDisposedException) { img.Dispose(); }
        }

        // ════════════════════════════════════════════════════════════
        //  JPEG ENCODE
        // ════════════════════════════════════════════════════════════
        private static readonly ImageCodecInfo _jpegCodec = FindJpegCodec();
        private static ImageCodecInfo FindJpegCodec()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        private static byte[] EncodeJpeg(Bitmap bmp, long quality)
        {
            using var ms = new MemoryStream();
            var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(ms, _jpegCodec, ep);
            return ms.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        //  ЗАВЕРШЕНИЕ
        // ════════════════════════════════════════════════════════════
        private void EndCall()
        {
            if (_ended) return;
            _ended = true;
            MarkCallEnded();
            Close();
        }

        private void MarkCallEnded()
        {
            if (_isCaller && !_callLogged)
            {
                LogCallToMessages();
            }

            try
            {
                using var conn = DBHelper.OpenConnection();
                // Удаляем себя из списка активных участников
                using (var del = new MySqlCommand("DELETE FROM call_participants WHERE call_id=@cid AND user_id=@uid", conn))
                {
                    del.Parameters.AddWithValue("@cid", _sessionId);
                    del.Parameters.AddWithValue("@uid", UserSession.EffectiveId);
                    del.ExecuteNonQuery();
                }

                // Проверяем, остались ли в звонке другие участники
                using (var cnt = new MySqlCommand("SELECT COUNT(*) FROM call_participants WHERE call_id=@cid", conn))
                {
                    cnt.Parameters.AddWithValue("@cid", _sessionId);
                    int activeCount = Convert.ToInt32(cnt.ExecuteScalar());

                    // Если в звонке никого не осталось (вышли все пользователи), завершаем саму сессию
                    if (activeCount == 0)
                    {
                        using var cmd = new MySqlCommand(
                            "UPDATE call_sessions SET status='ended', ended_at=NOW() WHERE id=@id AND status IN ('ringing','active')", conn);
                        cmd.Parameters.AddWithValue("@id", _sessionId);
                        cmd.ExecuteNonQuery();
                        WebSocketSignalingClient.Instance.SendMessage("call_status", _peerId, _sessionId, "ended");
                    }
                }
            }
            catch { }
        }

        private void LogCallToMessages()
        {
            if (_callLogged) return;
            _callLogged = true;

            try
            {
                int durationSeconds = 0;
                string msgType = "call_missed";
                string defaultText = "Пропущенный/Отмененный звонок";

                if (_connected && _startTime != default)
                {
                    msgType = "call_success";
                    durationSeconds = (int)(DateTime.Now - _startTime).TotalSeconds;
                    if (durationSeconds < 0) durationSeconds = 0;

                    TimeSpan t = TimeSpan.FromSeconds(durationSeconds);
                    defaultText = $"Звонок завершен ({t.Minutes}м {t.Seconds}с)";
                }

                using var conn = DBHelper.OpenConnection();

                if (_groupId >= 0)
                {
                    string query = @"INSERT INTO group_messages 
                        (group_id, sender_id, text, created_at) 
                        VALUES 
                        (@g_id, @s_id, @txt, NOW())";
                    using var cmd = new MySqlCommand(query, conn);
                    cmd.Parameters.AddWithValue("@g_id", _groupId);
                    cmd.Parameters.AddWithValue("@s_id", UserSession.EffectiveId);
                    cmd.Parameters.AddWithValue("@txt", defaultText);
                    cmd.ExecuteNonQuery();
                    WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _groupId, "group");
                }
                else
                {
                    string query = @"INSERT INTO messages 
                        (sender_id, receiver_id, text, is_read, created_at, msg_type, call_duration) 
                        VALUES 
                        (@s_id, @r_id, @txt, 0, NOW(), @m_type, @dur)";
                    using var cmd = new MySqlCommand(query, conn);
                    cmd.Parameters.AddWithValue("@s_id", UserSession.EffectiveId);
                    cmd.Parameters.AddWithValue("@r_id", _peerId);
                    cmd.Parameters.AddWithValue("@txt", defaultText);
                    cmd.Parameters.AddWithValue("@m_type", msgType);
                    cmd.Parameters.AddWithValue("@dur", (msgType == "call_success") ? (object)durationSeconds : DBNull.Value);
                    cmd.ExecuteNonQuery();
                    WebSocketSignalingClient.Instance.SendMessage("new_message", _peerId, 0, "direct");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG CALL ERROR] {ex.Message}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                WebSocketSignalingClient.Instance.OnMessageReceived -= OnWebSocketMessage;
                if (!_ended) MarkCallEnded();

                _signalTimer?.Stop(); _signalTimer?.Dispose();
                _durationTimer?.Stop(); _durationTimer?.Dispose();

                StopScreenShare();
                if (_cameraStarted)
                {
                    try { _transport?.StopCameraTrack(); } catch { }
                    _cameraStarted = false;
                }

                _pbLocal.Image?.Dispose();
                _pbRemote.Image?.Dispose();
                _pbRemoteCamera.Image?.Dispose();
                _transport?.Dispose();
            }
            catch { }

            base.OnFormClosing(e);
        }

        // Обработчик горячих клавиш: Ctrl+Alt+1..4 — разрешение (1080/720/480/360)
        //                        Ctrl+Shift+1..4 — FPS (60/45/30/15)
        private void CallForm_KeyDown(object? sender, KeyEventArgs e)
        {
            try
            {
                if (e.Control && e.Alt)
                {
                    int res = e.KeyCode switch
                    {
                        Keys.D1 => 1080,
                        Keys.D2 => 720,
                        Keys.D3 => 480,
                        Keys.D4 => 360,
                        _ => -1
                    };
                    if (res > 0)
                    {
                        ApplyHotSettings(res: res, fps: -1);
                        e.Handled = true;
                    }
                }
                else if (e.Control && e.Shift)
                {
                    int fps = e.KeyCode switch
                    {
                        Keys.D1 => 60,
                        Keys.D2 => 45,
                        Keys.D3 => 30,
                        Keys.D4 => 15,
                        _ => -1
                    };
                    if (fps > 0)
                    {
                        ApplyHotSettings(res: -1, fps: fps);
                        e.Handled = true;
                    }
                }
            }
            catch { }
        }

        // Применяет изменение разрешения/фпс "на горячую" и сохраняет в DeviceSettings.
        private void ApplyHotSettings(int res = -1, int fps = -1)
        {
            if (res > 0) DeviceSettings.ScreenShareResolutionHeight = res;
            if (fps > 0) DeviceSettings.ScreenShareFps = Math.Clamp(fps, 1, 60);
            try { DeviceSettings.Save(); } catch { }

            // Краткое уведомление в статусе (2 секунды), потом восстанавливаем предыдущее сообщение.
            string prevStatus = _lblStatus.Text;
            _lblStatus.Text = $"Настройки экрана: {DeviceSettings.ScreenShareResolutionHeight}p · {DeviceSettings.ScreenShareFps}fps";
            var tmp = new System.Windows.Forms.Timer { Interval = 2000 };
            tmp.Tick += (s, e) =>
            {
                tmp.Stop(); tmp.Dispose();
                if (!_connected) _lblStatus.Text = _isCaller ? "Вызов…" : "Подключение…";
                else _lblStatus.Text = "Соединение установлено";
            };
            tmp.Start();
        }

    }
}