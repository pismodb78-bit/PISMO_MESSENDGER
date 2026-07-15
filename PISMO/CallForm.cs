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
        // Режим голосового канала сервера (постоянная LiveKit-комната, без
        // call_sessions/ringing). _channelRoom задаёт имя комнаты.
        private readonly bool _isChannel;
        private readonly string _channelRoom;
        private int _vchId = -1;     // id голосового канала (для voice_presence)
        private int _vchTick = 0;    // троттлинг heartbeat в секундном таймере
        private int _partsTick = 0;  // троттлинг опроса участников (раз в 3 c)
        private bool _partsBusy;     // опрос участников уже идёт (не наслаиваем)
        // НАТИВНЫЙ транспорт LiveKit (livekit_ffi.dll) вместо WebView2 —
        // тот же контракт, что был у WebRtcTransport, но без Chromium (обход
        // 0x8007139F от VR). Мост переводит BGRA-кадры LiveKit в картинки-байты.
        private NativeCallBridge _transport = null;
        private System.Windows.Forms.Timer _signalTimer = null;  // ← явная инициализация
        private System.Windows.Forms.Timer _durationTimer = null;  // ← явная инициализация
        private DateTime _startTime;
        private bool _connected = false;
        private bool _ended = false;
        private bool _callLogged = false;


        // Аудио (микрофон + воспроизведение голоса/звука демки) полностью на
        // стороне LiveKit — NAudio для звонка больше не используется.
        // Громкость звука демонстрации экрана собеседника (0.0 - 1.0), отдельно от голоса
        private float _remoteScreenAudioVolume = 1.0f;
        private TrackBar _tbScreenAudioVolume;
        private Label _lblScreenAudioVolume;
        private bool _muted = false;

        // Камера теперь как настоящий WebRTC video track (getUserMedia внутри
        // WebRtcTransport), а не AForge VideoCaptureDevice + JPEG-over-DataChannel.
        private bool _cameraOff = true;  // камера по умолчанию ВЫКЛЮЧЕНА при входе в звонок
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
        private Label _lblPing;
        private Label _lblName;
        private Button _btnMute;
        private Button _btnDeafen;   // 🎧 полный мут: динамики + микрофон
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

        // ── Конструктор для голосового канала сервера ───────────────
        public CallForm(string channelRoom, string channelTitle)
        {
            _isChannel = true;
            _channelRoom = channelRoom;
            _vchId = VoicePresence.ChannelIdFromRoom(channelRoom);
            // Сразу отмечаемся «в эфире», чтобы другие увидели нас без задержки.
            if (_vchId > 0)
                System.Threading.Tasks.Task.Run(() =>
                    VoicePresence.Heartbeat(_vchId, UserSession.EffectiveId));
            _sessionId = -1;
            _isCaller = false;
            _peerName = channelTitle;
            _peerId = -1;
            _hasVideo = false;
            _groupId = 0; // как групповой: не завершаем при уходе одного участника

            _durationTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            BuildUi();
            StartCallSetup();
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
            if (_lblPing != null) _lblPing.Location = new Point(w - 260, 32);
            _lblZoom.Location = new Point(8, _pbRemote.Bottom - 24);

            _lblScreenAudioVolume.Location = new Point(w - 150, 70);
            _tbScreenAudioVolume.Location = new Point(w - 150, 90);

            int btnCount = 6;
            int btnW = 56, btnH = 56, gap = 12;
            int totalW = btnCount * btnW + (btnCount - 1) * gap;
            int startX = (_pnlButtons.Width - totalW) / 2;
            int btnY = (_pnlButtons.Height - btnH) / 2;
            var btns = new Button[] { _btnMute, _btnDeafen, _btnCamera, _btnScreen, _btnAudio, _btnHangup };
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
        /// <summary>Имя комнаты LiveKit — общее для всех участников одного звонка
        /// (или голосового канала сервера).</summary>
        private string RoomName => _isChannel ? _channelRoom : "call_" + _sessionId;

        private async void StartCallSetup()
        {
            // Плиточная сетка участников (Discord-style).
            BuildTilesHost();

            _transport = new NativeCallBridge();
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
            _transport.RemoteTileFrame += (pid, source, frame) => OnTileFrameOffThread(pid, source, frame);

            // --- «Смотреть стрим»: подключение к идущей демке по кнопке ---
            _transport.RemoteStreamPublished += (pid, name) => UiInvoke(() => OnStreamPublished(pid, name));
            _transport.RemoteStreamUnpublished += pid => UiInvoke(() => OnStreamUnpublished(pid));
            _transport.WatchFailed += (pid, err) => UiInvoke(() => OnWatchFailed(pid, err));
            // Краш рендера WebView: раньше — чёрный экран без звука и «не выйти».
            // Теперь выходим из театра, сообщаем и закрываем мёртвый звонок.
            _transport.RendererCrashed += kind => UiInvoke(() =>
            {
                try { ExitTheaterMode(); } catch { }
                try
                {
                    MessageBox.Show(this,
                        "Видеодвижок звонка аварийно завершился (" + kind + ").\n" +
                        "Звонок будет закрыт — перезайдите в него.",
                        "PISMO — звонок", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
                try { Close(); } catch { }
            });
            // GPU-процесс упал и восстановился (частый случай при захвате VR/
            // виртуальных рабочих столов): звонок ЖИВ, обрывается только демка —
            // откатываем её кнопку/бейдж, звонок не трогаем.
            _transport.ScreenEngineRecovered += () => UiInvoke(() =>
            {
                if (_screenSharing || _screenPreviewPending)
                    OnLocalScreenError("демонстрация прервалась (перезапуск видеодвижка). Звонок продолжается — включите демонстрацию заново");
            });
            _transport.StreamWatchersChanged += n => UiInvoke(() => OnStreamWatchers(n));
            _transport.ScreenSourceSwitched += (ok, err) => UiInvoke(() => OnScreenSourceSwitched(ok, err));
            _transport.ActiveSpeakers += json => UiInvoke(() => OnActiveSpeakers(json));
            _transport.PingUpdated += ms => UiInvoke(() => UpdatePing(ms));
            _transport.ParticipantRenamed += (pid, name) => UiInvoke(() => OnParticipantRenamed(pid, name));

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
                PushVoiceState();
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
            _transport.TheaterExitRequested += () => UiInvoke(ExitTheaterMode);
            _transport.TheaterFullscreenToggle += () => UiInvoke(ToggleTheaterFullscreen);
            // Диагностика демки в окне звонка убрана (просьба 2.1): цифры отправки
            // живут ТОЛЬКО в плашке PIP-превью стримера, приёма — в чипе театра.
            _transport.ScreenSendStats += t => UiInvoke(() => UpdatePipStats(t));

            // ПКМ по кнопке демонстрации — смена источника на лету (игра ↔ экран).
            try
            {
                var screenMenu = new ContextMenuStrip();
                screenMenu.Items.Add("🔁 Сменить источник (игра / весь экран)", null,
                    (s, e) => { if (_screenSharing) try { _transport?.SwitchScreenSource(); } catch { } });
                screenMenu.Opening += (s, e) => { e.Cancel = !_screenSharing; };
                _btnScreen.ContextMenuStrip = screenMenu;
            }
            catch { }
            // Предупреждение «кодируется процессором» и строка «Демонстрация:
            // WxH @ fps» в статусе убраны — только Debug-лог (просьба 2.1).
            _transport.SoftwareEncoderDetected += () =>
                System.Diagnostics.Debug.WriteLine("[SCREEN] программный энкодер (NVENC/QuickSync не задействован)");
            _transport.ScreenCaptureInfo += (fps, w, h) =>
                System.Diagnostics.Debug.WriteLine($"[SCREEN] захват {w}×{h} @ {fps} fps");

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
                // Тихий лог в %LOCALAPPDATA%\PISMO\call-error.log — чтобы при
                // повторе 0x8007139F видеть ТИП/HRESULT/стек. Ничего не показывает,
                // окна не плодит; только строка статуса + запись в файл.
                string logPath = "";
                try
                {
                    logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PISMO", "call-error.log");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
                    int hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
                    System.IO.File.AppendAllText(logPath,
                        $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n" +
                        $"Type   : {ex.GetType().FullName}\r\n" +
                        $"HRESULT: 0x{hr:X8}\r\n" +
                        $"Message: {ex.Message}\r\n" +
                        $"Inner  : {ex.InnerException?.GetType().FullName} / {ex.InnerException?.Message}\r\n" +
                        $"Stack  :\r\n{ex}\r\n\r\n");
                }
                catch { }
                UiInvoke(() => _lblStatus.Text = $"Ошибка звонка: {ex.Message}");
            }

            WebSocketSignalingClient.Instance.OnMessageReceived += OnWebSocketMessage;

            // Камеру стартуем только после установления соединения (OnConnected),
            // чтобы превью открывалось, когда комната уже готова принять трек.
            if (_hasVideo)
                _pendingVideoStart = true;

            // Для голосового канала сервера статусов звонка нет — пропускаем опрос.
            if (!_isChannel)
            {
                // Опрос статуса — фолбэк для корректного закрытия формы при
                // отклонении/завершении звонка собеседником (мгновенно это
                // приходит по WS call_status; опрос подстраховывает при обрыве).
                _signalTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                _signalTimer.Tick += (s, e) => PollCallStatus();
                _signalTimer.Start();
            }
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
        private bool _statusPollBusy;   // запрос уже идёт — не наслаиваем

        private void PollCallStatus()
        {
            if (_ended || _statusPollBusy) return;
            _statusPollBusy = true;

            // ВАЖНО: запрос статуса — в фоне. Раньше он шёл каждые 800 мс прямо
            // на UI-потоке — из-за этого во время личного звонка всё интерфейс
            // подлагивал (а после выхода из звонка «отпускало»).
            int sid = _sessionId;
            System.Threading.Tasks.Task.Run(() =>
            {
                string status = null;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "SELECT status FROM call_sessions WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@id", sid);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                        status = reader["status"]?.ToString();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CALL POLL ERROR] {ex.Message}");
                }
                finally { _statusPollBusy = false; }

                if (status != "rejected" && status != "ended") return;
                UiInvoke(() =>
                {
                    if (_ended) return;
                    _lblStatus.Text = status == "rejected" ? "Звонок отклонён" : "Завершён";
                    if (!_ended) MarkCallEnded();
                    _ended = true;
                    _signalTimer?.Stop();

                    var t = new System.Windows.Forms.Timer { Interval = 1200 };
                    t.Tick += (_, __) => { t.Stop(); if (!IsDisposed) Close(); };
                    t.Start();
                });
            });
        }

        private void OnConnected()
        {
            if (_connected) return;
            _connected = true;
            _startTime = DateTime.Now;
            _tilesReadyAt = DateTime.UtcNow; // с этого момента вход/выход озвучиваем
            _lblStatus.Text = "Соединение установлено";
            _durationTimer.Start();
            try { Sounds.CallConnected(); } catch { }

            // Применяем состояние кнопок голосового дока (футер MainForm):
            // мьют микрофона/«наушники» действуют и на новый звонок, плюс
            // сохранённое устройство вывода.
            try
            {
                if (VoiceState.MicMuted) SetMicMutedPublic(true);
                if (VoiceState.Deafened) SetAllMutedPublic(true);
                if (!string.IsNullOrWhiteSpace(DeviceSettings.SpeakerName))
                    _transport?.SetOutputDevice(DeviceSettings.SpeakerName);
                _transport?.SetScreenCodec(DeviceSettings.ScreenShareCodec);   // HEVC c авто-откатом
            }
            catch { }

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
            try { Sounds.ScreenOn(); } catch { }
        }

        private void OnLocalScreenStopped()
        {
            _screenSharing = false;
            PushVoiceState();
            _btnScreen.BackColor = Color.FromArgb(64, 68, 75);
            _btnScreen.Text = "🖥";
            _lblScreenBadge.Visible = false;
            try { Sounds.ScreenOff(); } catch { }
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

        /// <summary>Итог смены источника демонстрации «на лету». При любом исходе
        /// стрим продолжает идти: успех — уже новым источником, отмена/ошибка —
        /// прежним (состояние демки не трогаем, только возвращаем WebView).</summary>
        private void OnScreenSourceSwitched(bool ok, string error)
        {
            try { _transport?.HideTransportWindow(); } catch { }
            if (ok)
            {
                _lblStatus.Text = "Источник демонстрации изменён";
                return;
            }
            bool userCancel = error != null &&
                (error.Contains("NotAllowedError") || error.Contains("отмен") || error.Contains("таймаут"));
            if (!userCancel && !string.IsNullOrEmpty(error))
                _lblStatus.Text = "Смена источника: " + error;
        }

        /// <summary>Сколько человек сейчас смотрят нашу демку — в заголовок PIP.</summary>
        private void OnStreamWatchers(int n)
        {
            if (_screenPipTitleLbl == null || _screenPipTitleLbl.IsDisposed) return;
            try
            {
                _screenPipTitleLbl.Text = n > 0
                    ? $"🖥 Ваша демонстрация · 👁 {n}"
                    : "🖥 Ваша демонстрация";
            }
            catch { }
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
            _btnCamera.Text = "📷";
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
        // Единственная точка смены состояния микрофона: кнопка/хоткей/футер/
        // полный мут — все идут сюда. Кнопка красится ТОЛЬКО тут, транспорт
        // дёргается ТОЛЬКО тут — состояние UI не может разойтись со звуком.
        private void SetMicState(bool muted)
        {
            if (_muted == muted) { PaintMicButton(); return; }
            _muted = muted;
            PaintMicButton();
            try { _transport?.SetMicrophoneEnabled(!_muted); } catch { }
            try { if (_muted) Sounds.MicOff(); else Sounds.MicOn(); } catch { }
            VoiceState.MicMuted = _muted;
            try { MainForm.Current?.SyncFooterVoiceButtons(); } catch { }
        }

        private void PaintMicButton()
        {
            if (_btnMute == null) return;
            _btnMute.Text = _muted ? "🔇" : "🎤";
            _btnMute.BackColor = _muted
                ? Color.FromArgb(240, 71, 71) : Color.FromArgb(64, 68, 75);
        }

        private void ToggleMute()
        {
            bool willUnmute = _muted;
            SetMicState(!_muted);
            // Включение микрофона ИЗ ПОЛНОГО МУТА снимает и «наушники» —
            // работает микрофон и динамики (как просили / как в Discord).
            if (willUnmute && _remoteAllMuted) SetDeafenState(false);
        }

        // Единственная точка смены «наушников» (заглушить весь входящий звук).
        private void SetDeafenState(bool on)
        {
            if (_remoteAllMuted == on) { PaintDeafenButton(); return; }
            _remoteAllMuted = on;
            PaintDeafenButton();
            try { _transport?.SetRemoteMuted(on); } catch { }
            try { if (on) Sounds.MicOff(); else Sounds.MicOn(); } catch { }
            VoiceState.Deafened = on;
            try { MainForm.Current?.SyncFooterVoiceButtons(); } catch { }
        }

        private void PaintDeafenButton()
        {
            if (_btnDeafen == null) return;
            _btnDeafen.Text = _remoteAllMuted ? "🔕" : "🎧";
            _btnDeafen.BackColor = _remoteAllMuted
                ? Color.FromArgb(240, 71, 71) : Color.FromArgb(64, 68, 75);
        }

        /// <summary>Кнопка 🎧 «полный мут»: первое нажатие глушит динамики И
        /// микрофон; повторное — возвращает динамики (собеседников слышно),
        /// микрофон ОСТАЁТСЯ выключенным. Включить всё разом — кнопка 🎤.</summary>
        private void ToggleDeafenButton()
        {
            if (!_remoteAllMuted)
            {
                SetDeafenState(true);
                SetMicState(true);
            }
            else SetDeafenState(false);
        }

        /// <summary>Полный мут по хоткею — та же семантика, что у кнопки 🎧.</summary>
        private void ToggleDeafen() => ToggleDeafenButton();

        // ── Публичное API для голосового дока в MainForm (кнопки в футере) ──

        /// <summary>Текущий пинг (мс) — для показа по клику на «радар» в доке.</summary>
        public int CurrentPingMs { get; private set; }

        // (Стат-плашка демонстрации в углу окна звонка удалена по просьбе 2.1 —
        //  цифры отправки видны в плашке PIP-превью, приёма — в чипе театра.)

        /// <summary>Мьют микрофона включён?</summary>
        public bool MicMuted => _muted;

        /// <summary>Выключить/включить микрофон (из дока). Включение микрофона
        /// из полного мута снимает и «наушники».</summary>
        public void SetMicMutedPublic(bool muted)
        {
            SetMicState(muted);
            if (!muted && _remoteAllMuted) SetDeafenState(false);
        }

        /// <summary>Полный мут из дока: включение глушит и микрофон; выключение
        /// возвращает только динамики (микрофон остаётся выключенным).</summary>
        public void SetAllMutedPublic(bool muted)
        {
            if (muted)
            {
                SetDeafenState(true);
                SetMicState(true);
            }
            else SetDeafenState(false);
        }

        /// <summary>Сменить устройство ввода на лету (из дока).</summary>
        public void SetInputDeviceLive(string label) { try { _transport?.SetInputDevice(label); } catch { } }

        /// <summary>Сменить устройство вывода на лету (из дока).</summary>
        public void SetOutputDeviceLive(string label) { try { _transport?.SetOutputDevice(label); } catch { } }

        /// <summary>Вкл/выкл шумодав на лету (из дока, «эквалайзер»).</summary>
        public void SetNoiseSuppressionLive(bool on) { try { _transport?.SetNoiseSuppression(on); } catch { } }

        /// <summary>Попап с живым графиком задержки (клик по плашке 📶, как в Discord).</summary>
        private void TogglePingGraph()
        {
            if (_pingPopup != null && !_pingPopup.IsDisposed) { _pingPopup.Close(); _pingPopup = null; return; }

            var pop = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(30, 31, 34),
                ClientSize = new Size(300, 190),
                TopMost = true
            };
            try { pop.Location = PointToScreen(new Point(_lblPing.Left - 40, _lblPing.Bottom + 6)); } catch { }

            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 31, 34) };
            canvas.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var hist = _pingHistory;
                var rect = new Rectangle(12, 12, canvas.Width - 54, 92);

                // Сетка и шкала (0 / 50 / 100+).
                int max = 100;
                foreach (var v in hist) if (v > max) max = v;
                using (var grid = new Pen(Color.FromArgb(55, 57, 62)))
                using (var f8 = new Font("Segoe UI", 7.5f))
                using (var dim = new SolidBrush(Color.FromArgb(150, 152, 158)))
                {
                    for (int i = 0; i <= 2; i++)
                    {
                        int y = rect.Top + rect.Height * i / 2;
                        g.DrawLine(grid, rect.Left, y, rect.Right, y);
                        g.DrawString((max - max * i / 2).ToString(), f8, dim, rect.Right + 4, y - 6);
                    }
                }

                // Линия пинга.
                if (hist.Count >= 2)
                {
                    var pts = new PointF[hist.Count];
                    for (int i = 0; i < hist.Count; i++)
                    {
                        float x = rect.Left + rect.Width * i / (float)(hist.Count - 1);
                        float y = rect.Bottom - rect.Height * Math.Min(hist[i], max) / (float)max;
                        pts[i] = new PointF(x, y);
                    }
                    using var pen = new Pen(Color.FromArgb(88, 101, 242), 2f);
                    g.DrawLines(pen, pts);
                }

                // Средняя/последняя задержка.
                int avg = 0; foreach (var v in hist) avg += v;
                if (hist.Count > 0) avg /= hist.Count;
                int last = hist.Count > 0 ? hist[^1] : 0;
                using var fB = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
                using var fN = new Font("Segoe UI", 9f);
                using var white = new SolidBrush(Color.FromArgb(230, 231, 232));
                // Значения рисуем ПОСЛЕ самой длинной подписи (замер, не константа) —
                // иначе «Последняя задержка:» наползала на число.
                float labelW = Math.Max(
                    g.MeasureString("Средняя задержка:", fN).Width,
                    g.MeasureString("Последняя задержка:", fN).Width);
                float valX = 12 + labelW + 8;
                g.DrawString("Средняя задержка:", fN, white, 12, 116);
                g.DrawString($"{avg} мс", fB, white, valX, 116);
                g.DrawString("Последняя задержка:", fN, white, 12, 138);
                g.DrawString($"{last} мс", fB, white, valX, 138);
                using var hint = new SolidBrush(Color.FromArgb(150, 152, 158));
                using var f8b = new Font("Segoe UI", 7.5f);
                g.DrawString("При задержке 250 мс и больше звук может отставать.", f8b, hint, 12, 164);
            };
            pop.Controls.Add(canvas);

            // Живое обновление, закрытие при потере фокуса/повторном клике.
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += (s, e) => { try { canvas.Invalidate(); } catch { } };
            t.Start();
            pop.Deactivate += (s, e) => { try { pop.Close(); } catch { } };
            pop.FormClosed += (s, e) => { t.Stop(); t.Dispose(); if (_pingPopup == pop) _pingPopup = null; };

            _pingPopup = pop;
            pop.Show(this);
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
                ClientSize = new Size(310, 510)
            };
            var anchor = PointToScreen(new Point(_pnlButtons.Left, _pnlButtons.Top));
            _audioPanel.Location = new Point(
                Math.Max(0, anchor.X + (_pnlButtons.Width - 310) / 2),
                Math.Max(0, anchor.Y - 520));

            int y = 12;
            Label MkLbl(string t)
            {
                var l = new Label { Text = t, ForeColor = Color.FromArgb(220, 221, 222), AutoSize = true, Location = new Point(14, y), Font = new Font("Segoe UI", 9f) };
                _audioPanel.Controls.Add(l); y += 22; return l;
            }
            ComboBox MkCombo()
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, y), Size = new Size(282, 24), FlatStyle = FlatStyle.Flat };
                _audioPanel.Controls.Add(cb); y += 38; return cb;
            }
            TrackBar MkTb(int val)
            {
                var tb = new TrackBar { Minimum = 0, Maximum = 300, Value = Math.Min(300, val), TickStyle = TickStyle.None, Location = new Point(8, y), Size = new Size(290, 40) };
                _audioPanel.Controls.Add(tb); y += 58; return tb;
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
            cmbRes.Items.AddRange(new object[] { "Исходное", "1080p", "720p", "480p", "360p" });
            cmbFps.Items.AddRange(new object[] { "60 fps", "30 fps", "15 fps" });
            cmbRes.SelectedItem = DeviceSettings.ScreenShareResolutionHeight > 0
                ? DeviceSettings.ScreenShareResolutionHeight + "p" : "Исходное";
            if (cmbRes.SelectedIndex < 0) cmbRes.SelectedIndex = 0;
            cmbFps.SelectedItem = DeviceSettings.ScreenShareFps + " fps";
            if (cmbFps.SelectedIndex < 0) cmbFps.SelectedIndex = 1;
            cmbRes.SelectedIndexChanged += (s, e) =>
            {
                string sel = (string)cmbRes.SelectedItem;
                int h = sel == "Исходное" ? 0 : int.Parse(sel.Replace("p", ""));
                DeviceSettings.ScreenShareResolutionHeight = h; try { DeviceSettings.Save(); } catch { }
                // Применяем к ИДУЩЕЙ демонстрации сразу (не только «при следующем запуске»).
                try { _transport?.SetScreenQualityLive(DeviceSettings.ScreenShareResolutionHeight, DeviceSettings.ScreenShareFps); } catch { }
            };
            cmbFps.SelectedIndexChanged += (s, e) =>
            {
                int f = int.Parse(((string)cmbFps.SelectedItem).Replace(" fps", ""));
                DeviceSettings.ScreenShareFps = f; try { DeviceSettings.Save(); } catch { }
                try { _transport?.SetScreenQualityLive(DeviceSettings.ScreenShareResolutionHeight, DeviceSettings.ScreenShareFps); } catch { }
            };
            _audioPanel.Controls.Add(cmbRes);
            _audioPanel.Controls.Add(cmbFps);
            y = rowQ + 34;
            var lblHint = new Label { Text = "(применяется сразу, в том числе к идущей демонстрации)", ForeColor = Color.FromArgb(140, 142, 148), AutoSize = true, Location = new Point(14, y), Font = new Font("Segoe UI", 7.5f) };
            _audioPanel.Controls.Add(lblHint); y += 24;

            MkLbl("Кодек демонстрации");
            var cmbCodec = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, y), Size = new Size(282, 24), FlatStyle = FlatStyle.Flat };
            cmbCodec.Items.AddRange(new object[] { "H.265 / HEVC (чётче, с откатом на H.264)", "H.264 (совместимость)" });
            cmbCodec.SelectedIndex = DeviceSettings.ScreenShareCodec == "h264" ? 1 : 0;
            cmbCodec.SelectedIndexChanged += (s, e) =>
            {
                DeviceSettings.ScreenShareCodec = cmbCodec.SelectedIndex == 1 ? "h264" : "h265";
                try { DeviceSettings.Save(); } catch { }
                try { _transport?.SetScreenCodec(DeviceSettings.ScreenShareCodec); } catch { }
            };
            _audioPanel.Controls.Add(cmbCodec);
            y += 30;
            var lblCodecHint = new Label { Text = "(смена кодека применится при следующем запуске демонстрации)", ForeColor = Color.FromArgb(140, 142, 148), AutoSize = true, Location = new Point(14, y), Font = new Font("Segoe UI", 7.5f) };
            _audioPanel.Controls.Add(lblCodecHint); y += 24;

            MkLbl("Видеокарта для кодирования");
            var cmbGpu = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(14, y), Size = new Size(282, 24), FlatStyle = FlatStyle.Flat };
            cmbGpu.Items.AddRange(new object[] { "Авто (как в Windows)", "Дискретная (RTX/GTX, NVENC)", "Встроенная (Intel Quick Sync)" });
            cmbGpu.SelectedIndex = DeviceSettings.GpuEncodePref == "high" ? 1 : DeviceSettings.GpuEncodePref == "integrated" ? 2 : 0;
            cmbGpu.SelectedIndexChanged += (s, e) =>
            {
                DeviceSettings.GpuEncodePref = cmbGpu.SelectedIndex == 1 ? "high" : cmbGpu.SelectedIndex == 2 ? "integrated" : "auto";
                try { DeviceSettings.Save(); } catch { }
                System.Threading.Tasks.Task.Run(() => { try { GpuPreference.Apply(DeviceSettings.GpuEncodePref); } catch { } });
            };
            _audioPanel.Controls.Add(cmbGpu);
            y += 30;
            var lblGpuHint = new Label { Text = "(вступит в силу после перезапуска приложения; для MX-карт без NVENC выбирайте «Встроенная»)", ForeColor = Color.FromArgb(140, 142, 148), AutoSize = false, Size = new Size(300, 28), Location = new Point(14, y), Font = new Font("Segoe UI", 7.5f) };
            _audioPanel.Controls.Add(lblGpuHint); y += 32;
            // (Диагностическое окно нативного NVENC убрано из сборки — потолок
            //  демонстрации на этой машине определяется частотой ЗАХВАТА экрана
            //  ~50fps, а не энкодером, поэтому нативный путь fps не поднимает.)

            MkLbl("Громкость собеседников");
            var tbVoice = MkTb((int)(_remoteVoiceVolume * 100));
            tbVoice.ValueChanged += (s, e) => { _remoteVoiceVolume = tbVoice.Value / 100f; try { _transport?.SetRemoteVoiceVolume(_remoteVoiceVolume); } catch { } };

            MkLbl("Громкость демонстрации");
            var tbScreen = MkTb((int)(_remoteScreenAudioVolume * 100));
            tbScreen.ValueChanged += (s, e) => { _remoteScreenAudioVolume = tbScreen.Value / 100f; try { _transport?.SetRemoteScreenAudioVolume(_remoteScreenAudioVolume); } catch { } };

            var chkMute = new CheckBox { Text = "🔇 Заглушить весь звук", ForeColor = Color.FromArgb(220, 221, 222), AutoSize = true, Location = new Point(14, y), Checked = _remoteAllMuted, Font = new Font("Segoe UI", 9.5f) };
            chkMute.CheckedChanged += (s, e) => { _remoteAllMuted = chkMute.Checked; try { _transport?.SetRemoteMuted(_remoteAllMuted); } catch { } };
            _audioPanel.Controls.Add(chkMute);

            // КРИТИЧНО: при программном заполнении списков НЕ дёргаем смену
            // устройства — иначе открытие панели само включало камеру (зелёный
            // экран/предпросмотр не пропадал) и сбрасывало микрофон.
            bool populating = false;
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
                        populating = true;
                        cmbMic.Items.Clear(); cmbMic.Items.AddRange(mics);
                        cmbSpk.Items.Clear(); cmbSpk.Items.AddRange(spk);
                        cmbCam.Items.Clear(); cmbCam.Items.AddRange(cams);
                        if (!string.IsNullOrEmpty(DeviceSettings.MicrophoneName)) cmbMic.SelectedItem = DeviceSettings.MicrophoneName;
                        if (cmbMic.SelectedIndex < 0 && cmbMic.Items.Count > 0) cmbMic.SelectedIndex = 0;
                        if (cmbSpk.Items.Count > 0) cmbSpk.SelectedIndex = 0;
                        if (!string.IsNullOrEmpty(DeviceSettings.CameraName)) cmbCam.SelectedItem = DeviceSettings.CameraName;
                        if (cmbCam.SelectedIndex < 0 && cmbCam.Items.Count > 0) cmbCam.SelectedIndex = 0;
                        populating = false;
                    });
                }
                catch { }
            }
            _transport.DevicesEnumerated += OnDevices;
            cmbMic.SelectedIndexChanged += (s, e) => { if (populating) return; if (cmbMic.SelectedItem is string m) { DeviceSettings.MicrophoneName = m; try { DeviceSettings.Save(); } catch { } _transport?.SetInputDevice(m); } };
            cmbSpk.SelectedIndexChanged += (s, e) => { if (populating) return; if (cmbSpk.SelectedItem is string sp) _transport?.SetOutputDevice(sp); };
            cmbCam.SelectedIndexChanged += (s, e) =>
            {
                if (populating) return;
                if (cmbCam.SelectedItem is string cm)
                {
                    DeviceSettings.CameraName = cm; try { DeviceSettings.Save(); } catch { }
                    // Переключаем «живую» камеру только если она сейчас включена,
                    // иначе просто запоминаем выбор (не включаем захват).
                    if (_cameraStarted) _transport?.SwitchCameraDevice(cm);
                }
            };

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
                PushVoiceState();
                _transport.ConfirmCameraShare();
            };
            _cameraPreviewForm.Cancelled += () =>
            {
                _transport.DevicesEnumerated -= OnDevicesForPreview;
                _cameraPreviewForm = null;
                _transport.CancelCameraPreview();
                // Возвращаем кнопку в состояние "выключено" (🚫 + красный). Раньше
                // тут ошибочно ставился «включённый» вид (📷 + серый) — кнопка
                // показывала камеру включённой, хотя её отменили.
                _cameraOff = true;
                _cameraStarted = false;
                _btnCamera.Text = "📷";
                _btnCamera.BackColor = Color.FromArgb(240, 71, 71);
                var oldc = _pbLocal.Image; _pbLocal.Image = null; oldc?.Dispose();
                _pbLocal.Visible = false;
                try { OnSelfCameraStopped(); } catch { } // убрать плитку, если кадр проскочил
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

        /// <summary>Обновляет плашку пинга (RTT) и красит её по качеству связи.</summary>
        // История пинга для графика (клик по плашке 📶 — попап как в Discord).
        private readonly System.Collections.Generic.List<int> _pingHistory = new();
        private Form _pingPopup;

        private void UpdatePing(int ms)
        {
            CurrentPingMs = ms;
            _pingHistory.Add(ms);
            if (_pingHistory.Count > 150) _pingHistory.RemoveAt(0);
            if (_lblPing == null || _lblPing.IsDisposed) return;
            _lblPing.Text = $"📶 {ms} ms";
            _lblPing.ForeColor = ms < 80 ? Color.FromArgb(120, 220, 130)   // хорошо — зелёный
                               : ms < 180 ? Color.FromArgb(240, 200, 90)   // средне — жёлтый
                               : Color.FromArgb(240, 110, 100);            // плохо — красный
            if (!_lblPing.Visible) _lblPing.Visible = true;
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
                _btnCamera.Text = "📷";
                _btnCamera.BackColor = Color.FromArgb(240, 71, 71);
                _transport.StopCameraTrack();
                _cameraStarted = false;
                PushVoiceState();
                try { Sounds.CameraOff(); } catch { }
                var old = _pbLocal.Image; _pbLocal.Image = null; old?.Dispose();
            }
            else
            {
                _btnCamera.Text = "📷";
                _btnCamera.BackColor = Color.FromArgb(64, 68, 75);
                try { Sounds.CameraOn(); } catch { }
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
                // Сразу свой список ВСЕХ мониторов (Screen.AllScreens ловит все 3,
                // в т.ч. виртуальный VR-дисплей, который системный диалог Chromium
                // не перечисляет). Без промежуточного меню. Окна/другой источник —
                // кнопка внутри списка (там открывается системный диалог).
                PickMonitorAndShare();
            }
        }

        /// <summary>Свой выборщик (вкладки «Окно»/«Весь экран», все мониторы и окна) →
        /// захват выбранного источника. Без системного диалога Chromium — работает и
        /// при активном VR (путь к нативному захвату).</summary>
        private void PickMonitorAndShare()
        {
            try
            {
                using var picker = new ScreenPickerForm();
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                // Окно — точный захват через PrintWindow (берёт даже перекрытое);
                // монитор/область — BitBlt по экранным координатам.
                if (!picker.SelectedIsScreen && picker.SelectedWindow != IntPtr.Zero)
                {
                    _screenPreviewPending = true;
                    _transport.StartWindowShare(picker.SelectedWindow, DeviceSettings.ScreenShareFps);
                }
                else if (picker.SelectedBounds is Rectangle b)
                {
                    _screenPreviewPending = true;
                    _transport.StartMonitorShare(b, DeviceSettings.ScreenShareResolutionHeight, DeviceSettings.ScreenShareFps);
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "Не удалось открыть выбор экрана: " + ex.Message;
            }
        }

        /// <summary>Немедленно отправляет состояние «в эфире» (камера/демка) для
        /// голосового канала, чтобы бейдж у других обновился без задержки.</summary>
        private void PushVoiceState()
        {
            if (!_isChannel || _vchId <= 0) return;
            bool streaming = _cameraStarted || _screenSharing;
            int vch = _vchId, me = UserSession.EffectiveId;
            System.Threading.Tasks.Task.Run(() => VoicePresence.Heartbeat(vch, me, streaming));
        }

        private void StopScreenShare()
        {
            _screenSharing = false;
            _screenPreviewPending = false;
            PushVoiceState();

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
        private Label _screenPipTitleLbl;    // «Ваша демонстрация · 👁 N»
        private Label _screenPipStats;       // что реально уходит зрителям (fps/битрейт)
        private bool _screenPipCollapsed = false;
        private Size _screenPipExpandedSize = new Size(260, 170);
        private NotifyIcon _screenPipTrayIcon;

        /// <summary>Строка отправки в PIP: превью подстраивает свой темп под
        /// РЕАЛЬНЫЙ исходящий поток, а эта плашка показывает его цифрами.</summary>
        private void UpdatePipStats(string text)
        {
            if (_screenPipStats == null || _screenPipStats.IsDisposed) return;
            try
            {
                _screenPipStats.Text = text ?? "";
                _screenPipStats.Visible = !string.IsNullOrEmpty(text) && !_screenPipCollapsed;
            }
            catch { }
        }

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
            _screenPipTitleLbl = lbl;
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

            // Смена источника трансляции на лету (игра ↔ весь экран).
            var btnSwitch = new Button
            {
                Text = "🔁",
                Dock = DockStyle.Right,
                Width = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Emoji", 8f)
            };
            btnSwitch.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(btnSwitch, "Сменить источник (игра / весь экран)");
            btnSwitch.Click += (s, e) => { if (_screenSharing) try { _transport?.SwitchScreenSource(); } catch { } };

            _screenPipTitleBar.Controls.Add(lbl);
            _screenPipTitleBar.Controls.Add(btnSwitch);
            _screenPipTitleBar.Controls.Add(btnToggle);
            _screenPipTitleBar.Controls.Add(btnTray);

            _screenPipPicture = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 21, 23),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Плашка «что реально уходит зрителям» — превью живёт в том же
            // темпе/размере, а здесь цифры (fps, битрейт, упор CPU/сеть).
            _screenPipStats = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 17,
                Visible = false,
                ForeColor = Color.FromArgb(170, 173, 179),
                BackColor = Color.FromArgb(26, 27, 30),
                Font = new Font("Consolas", 7.25f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 0, 0),
                AutoEllipsis = true
            };

            _screenPipForm.Controls.Add(_screenPipPicture);
            _screenPipForm.Controls.Add(_screenPipStats);
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
            Anim.FadeIn(_screenPipForm);
        }

        private void ToggleScreenSharePipCollapsed()
        {
            if (_screenPipForm == null) return;
            _screenPipCollapsed = !_screenPipCollapsed;
            if (_screenPipCollapsed)
            {
                _screenPipExpandedSize = _screenPipForm.Size;
                _screenPipPicture.Visible = false;
                if (_screenPipStats != null) _screenPipStats.Visible = false;
                _screenPipForm.Size = new Size(180, 22);
                try { _transport?.SetScreenPreviewActive(false); } catch { }   // превью скрыто — не извлекаем кадры
            }
            else
            {
                _screenPipPicture.Visible = true;
                _screenPipForm.Size = _screenPipExpandedSize;
                try { _transport?.SetScreenPreviewActive(true); } catch { }
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
            try { _transport?.SetScreenPreviewActive(false); } catch { }   // в трее — превью не нужно

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
            if (!_screenPipCollapsed) try { _transport?.SetScreenPreviewActive(true); } catch { }
        }

        // (Предупреждение «демка кодируется процессором» с балуном из трея
        //  удалено по просьбе 2.1 — факт софт-энкода виден в плашке PIP и Debug-логе.)

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
            _screenPipTitleLbl = null;
            _screenPipStats = null;
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
            try { Sounds.Hangup(); } catch { }
            MarkCallEnded();
            Close();
        }

        private void MarkCallEnded()
        {
            if (_isChannel) return; // у голосового канала нет call_sessions/логов

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

        // ── Глобальные горячие клавиши (микрофон/камера/демка) ──────────
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private const int WM_HOTKEY = 0x0312;
        private bool _hotkeysRegistered;

        private static (uint mods, uint vk) SplitHotkey(int value)
        {
            var k = (Keys)value;
            uint mods = 0;
            if ((k & Keys.Alt) == Keys.Alt) mods |= 0x1;       // MOD_ALT
            if ((k & Keys.Control) == Keys.Control) mods |= 0x2; // MOD_CONTROL
            if ((k & Keys.Shift) == Keys.Shift) mods |= 0x4;   // MOD_SHIFT
            uint vk = (uint)(k & Keys.KeyCode);
            return (mods, vk);
        }

        private void RegisterCallHotkeys()
        {
            if (_hotkeysRegistered) return;
            try
            {
                void Reg(int id, int val)
                {
                    if (val == 0) return;
                    var (m, vk) = SplitHotkey(val);
                    if (vk != 0) RegisterHotKey(Handle, id, m, vk);
                }
                Reg(1, DeviceSettings.HotkeyMic);
                Reg(2, DeviceSettings.HotkeyCamera);
                Reg(3, DeviceSettings.HotkeyScreen);
                Reg(4, DeviceSettings.HotkeyDeafen);
                _hotkeysRegistered = true;
            }
            catch { }
        }

        private void UnregisterCallHotkeys()
        {
            if (!_hotkeysRegistered) return;
            try { UnregisterHotKey(Handle, 1); UnregisterHotKey(Handle, 2); UnregisterHotKey(Handle, 3); UnregisterHotKey(Handle, 4); } catch { }
            _hotkeysRegistered = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterCallHotkeys();
        }

        // Применяем настройки чувствительности «на лету»: когда окно звонка
        // снова активно (например, после изменения в настройках) — отправляем
        // актуальный порог в транспорт, если он изменился.
        private bool _lastVoiceAuto;
        private int _lastVoiceThr = int.MinValue;
        private int _lastNoise = -1; // -1 не задано, 0/1
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try
            {
                if (_lastVoiceThr == int.MinValue ||
                    _lastVoiceAuto != DeviceSettings.VoiceAutoSensitivity ||
                    _lastVoiceThr != DeviceSettings.VoiceThreshold)
                {
                    _lastVoiceAuto = DeviceSettings.VoiceAutoSensitivity;
                    _lastVoiceThr = DeviceSettings.VoiceThreshold;
                    _transport?.SetVoiceGate(_lastVoiceAuto, _lastVoiceThr);
                }
                int ns = DeviceSettings.NoiseSuppression ? 1 : 0;
                if (_lastNoise != ns)
                {
                    _lastNoise = ns;
                    _transport?.SetNoiseSuppression(ns == 1);
                }
                if (Math.Abs(_lastGain - DeviceSettings.MicrophoneGain) > 0.001f)
                {
                    _lastGain = DeviceSettings.MicrophoneGain;
                    _transport?.SetMicGain(_lastGain);
                }
            }
            catch { }
        }
        private float _lastGain = -1f;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                switch (id)
                {
                    case 1: ToggleMute(); break;
                    case 2: ToggleCamera(); break;
                    case 3: ToggleScreen(); break;
                    case 4: ToggleDeafen(); break;
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { UnregisterCallHotkeys(); } catch { }
            try
            {
                WebSocketSignalingClient.Instance.OnMessageReceived -= OnWebSocketMessage;
                try { AvatarStore.AvatarLoaded -= OnAvatarLoadedForTiles; } catch { }
                if (!_ended) MarkCallEnded();

                // Убираем себя из «в эфире» голосового канала.
                if (_isChannel && _vchId > 0)
                {
                    int vch = _vchId, me = UserSession.EffectiveId;
                    System.Threading.Tasks.Task.Run(() => VoicePresence.Leave(vch, me));
                }

                _signalTimer?.Stop(); _signalTimer?.Dispose();
                _durationTimer?.Stop(); _durationTimer?.Dispose();
                _speakHoldTimer?.Stop(); _speakHoldTimer?.Dispose();

                CloseAllStreamPopouts();   // окна стримов не должны переживать звонок
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

            // Применяем к ЖИВОЙ демке (раньше настройка лишь сохранялась и до
            // перезапуска демонстрации ни на что не влияла).
            try { _transport?.SetScreenQualityLive(DeviceSettings.ScreenShareResolutionHeight, DeviceSettings.ScreenShareFps); } catch { }

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