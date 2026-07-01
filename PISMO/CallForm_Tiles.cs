using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Плиточная сетка участников звонка в стиле Discord. Каждый участник —
    /// плитка с камерой (или аватаром-заглушкой), именем и подсветкой, когда
    /// говорит. Демонстрация экрана участника — отдельная плитка.
    ///
    /// Кадры приходят из WebRtcTransport (LiveKit) по событиям RemoteTileFrame
    /// с привязкой к pid (id участника) и source (camera|screen).
    /// </summary>
    public partial class CallForm
    {
        private sealed class CallTile
        {
            public string Pid;
            public string Name;
            public string Source;      // "camera" | "screen"
            public Panel Panel;
            public PictureBox Pb;
            public Label Lbl;
            public bool Speaking;
            public bool HasVideo;
        }

        private Panel _tilesHost;
        private readonly Dictionary<string, CallTile> _tiles = new();
        private readonly List<string> _tileOrder = new();           // порядок плиток
        private readonly Dictionary<string, string> _participants = new(); // pid -> name
        private string SelfPid => UserSession.EffectiveId.ToString();

        // Удержание подсветки говорящего: рамка гаснет не сразу, а через HOLD после
        // последнего звука — чтобы при чтении текста (с микропаузами) горела ровно.
        private readonly Dictionary<string, DateTime> _speakUntil = new();
        private System.Windows.Forms.Timer _speakHoldTimer;
        // Сглаживание (attack/release) делает JS-детектор; здесь короткий хвост,
        // чтобы рамка не моргала между тиками детектора (он шлёт каждые ~60мс).
        private const int SpeakHoldMs = 200;

        private string _fullscreenKey;  // ключ плитки на весь экран (PictureBox), либо null
        private string _theaterKey;     // ключ плитки в нативном «театре» (WebView 60fps), либо null
        private bool _theaterFullscreen;                 // театр развёрнут на весь монитор
        private FormBorderStyle _savedBorder;            // сохранённое состояние окна для возврата
        private FormWindowState _savedWinState;
        private Rectangle _savedBounds;
        private readonly Dictionary<string, float> _userVol = new();   // pid -> громкость
        private readonly Dictionary<string, bool> _userMuted = new();  // pid -> заглушен
        private Form _userAudioPopup;

        private static string TileKey(string pid, string source) => pid + "|" + source;

        /// <summary>Создаёт контейнер плиток поверх старой области видео и прячет
        /// одиночные PictureBox'ы (теперь всё рисуется плитками).</summary>
        private void BuildTilesHost()
        {
            if (_tilesHost != null) return;

            _tilesHost = new Panel
            {
                BackColor = Color.FromArgb(24, 25, 28),
                Location = new Point(0, 56),
                Size = new Size(ClientSize.Width, Math.Max(50, ClientSize.Height - 56 - _pnlButtons.Height)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            _tilesHost.Resize += (s, e) =>
            {
                if (_theaterKey != null) { try { _transport?.UpdateTheaterBounds(TheaterBounds()); } catch { } }
                else LayoutTiles();
            };

            // Прячем старую одиночную раскладку видео. В режиме плиток эти
            // PictureBox'ы вообще не нужны — убираем их из формы, чтобы пустой
            // прямоугольник с рамкой (FixedSingle) не висел поверх плиток.
            try { _pbRemote.Visible = false; Controls.Remove(_pbRemote); } catch { }
            try { _pbLocal.Visible = false; Controls.Remove(_pbLocal); } catch { }
            try { _pbRemoteCamera.Visible = false; Controls.Remove(_pbRemoteCamera); } catch { }

            Controls.Add(_tilesHost);
            _tilesHost.SendToBack();

            // Поднимаем поверх плиток все накладки и панель кнопок.
            try
            {
                _pnlButtons.BringToFront();
                _pnlParticipants?.BringToFront();
                _lblName.BringToFront();
                _lblStatus.BringToFront();
                _lblDuration.BringToFront();
                _lblScreenBadge.BringToFront();
            }
            catch { }

            // Своя плитка камеры всегда присутствует (аватар, пока камера выключена).
            AddParticipantTile(SelfPid, "Вы");

            AvatarStore.AvatarLoaded += OnAvatarLoadedForTiles;
        }

        private void OnAvatarLoadedForTiles(int uid)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    string pid = uid.ToString();
                    foreach (var kv in _tiles)
                        if (kv.Value.Pid == pid && !kv.Value.HasVideo)
                            try { kv.Value.Panel.Invalidate(); } catch { }
                }));
            }
            catch { }
        }

        // Время входа в звонок — чтобы не пикать на каждого, кто уже был в канале
        // (первичное перечисление участников при заходе).
        private DateTime _tilesReadyAt = DateTime.UtcNow;

        // ── Участники ───────────────────────────────────────────────────
        private void AddParticipant(string pid, string name)
        {
            if (string.IsNullOrEmpty(pid) || pid == SelfPid) return;
            bool isNew = !_participants.ContainsKey(pid);
            _participants[pid] = name;
            AddParticipantTile(pid, name);
            // Звук «зашёл» — только для реально новых участников (не при входе в канал).
            if (isNew && (DateTime.UtcNow - _tilesReadyAt).TotalMilliseconds > 1500)
                try { Sounds.UserJoined(); } catch { }
        }

        private void RemoveParticipant(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return;
            bool existed = _participants.Remove(pid);
            RemoveTile(TileKey(pid, "camera"));
            RemoveTile(TileKey(pid, "screen"));
            LayoutTiles();
            if (existed && (DateTime.UtcNow - _tilesReadyAt).TotalMilliseconds > 1500)
                try { Sounds.UserLeft(); } catch { }
        }

        /// <summary>Гарантирует наличие плитки участника (камера-плитка = основная).</summary>
        private CallTile AddParticipantTile(string pid, string name)
        {
            return EnsureTile(pid, name, "camera");
        }

        private CallTile EnsureTile(string pid, string name, string source)
        {
            if (_tilesHost == null) return null;
            string key = TileKey(pid, source);
            if (_tiles.TryGetValue(key, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                return existing;
            }

            var tile = new CallTile { Pid = pid, Name = string.IsNullOrWhiteSpace(name) ? pid : name, Source = source };

            tile.Panel = new Panel { BackColor = Color.FromArgb(47, 49, 54) };
            tile.Pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(32, 34, 37),
                Visible = false
            };
            tile.Lbl = new Label
            {
                AutoSize = false,
                Height = 20,
                Dock = DockStyle.Bottom,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(160, 20, 21, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Text = (source == "screen" ? "🖥 " : "") + tile.Name
            };

            // Применяем сохранённую пользовательскую громкость/мьют для собеседника.
            if (source == "camera" && pid != SelfPid && UserAudioPrefs.Has(pid))
            {
                float sv = UserAudioPrefs.GetVolume(pid);
                bool sm = UserAudioPrefs.GetMuted(pid);
                _userVol[pid] = sv; _userMuted[pid] = sm;
                try { _transport?.SetParticipantVolume(pid, sv); } catch { }
                if (sm) try { _transport?.SetParticipantMuted(pid, true); } catch { }
            }

            string capPid = pid, capSource = source, capKey = key;
            tile.Panel.Paint += (s, e) => PaintTile(e.Graphics, tile);

            // Двойной клик — на весь экран; правый клик — громкость/мьют участника.
            void OnDouble(object s, EventArgs e) => ToggleFullscreen(capKey);
            void OnMouse(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && capPid != SelfPid)
                    ShowParticipantAudioMenu(capPid);
            }
            tile.Panel.DoubleClick += OnDouble;
            tile.Pb.DoubleClick += OnDouble;
            tile.Panel.MouseUp += OnMouse;
            tile.Pb.MouseUp += OnMouse;

            tile.Panel.Controls.Add(tile.Pb);
            tile.Panel.Controls.Add(tile.Lbl);
            tile.Lbl.BringToFront();

            _tilesHost.Controls.Add(tile.Panel);
            _tiles[key] = tile;
            _tileOrder.Add(key);
            LayoutTiles();
            return tile;
        }

        private void RemoveTile(string key)
        {
            if (_tiles.TryGetValue(key, out var tile))
            {
                try
                {
                    var old = tile.Pb.Image; tile.Pb.Image = null; old?.Dispose();
                    _tilesHost.Controls.Remove(tile.Panel);
                    tile.Panel.Dispose();
                }
                catch { }
                _tiles.Remove(key);
                _tileOrder.Remove(key);
                if (_fullscreenKey == key) _fullscreenKey = null;
                if (_theaterKey == key) ExitTheaterMode(); // демонстрацию прекратили — выходим из театра
            }
        }

        // ── События треков ──────────────────────────────────────────────
        private void OnTileStarted(string pid, string name, string source)
        {
            if (pid == SelfPid && source == "camera") return; // своя камера — отдельный поток кадров
            var tile = EnsureTile(pid, name, source);
            if (tile != null) tile.HasVideo = true;

            if (source == "screen")
            {
                _peerScreenSharing = true;
                _tbScreenAudioVolume.Visible = true;
                _lblScreenAudioVolume.Visible = true;
                _lblScreenBadge.Text = "🖥 Идёт демонстрация экрана";
                _lblScreenBadge.Visible = true;

                // Демонстрацию собеседника показываем СРАЗУ нативно (GPU-декод напрямую,
                // плавные 60fps) — без двойного клика. Выйти можно ✕/двойным кликом.
                if (pid != SelfPid && _theaterKey == null)
                {
                    _theaterKey = TileKey(pid, source);
                    var b = _tilesHost.Bounds;
                    try { _tilesHost.Visible = false; } catch { }
                    try { _transport?.EnterTheater(pid, "screen", b); } catch { }
                }
            }
        }

        private void OnTileStopped(string pid, string source)
        {
            string key = TileKey(pid, source);
            if (source == "screen")
            {
                RemoveTile(key);
                LayoutTiles();
                // Если больше никто не шарит экран — прячем бейдж/громкость.
                bool anyScreen = false;
                foreach (var k in _tileOrder) if (k.EndsWith("|screen")) { anyScreen = true; break; }
                if (!anyScreen)
                {
                    _peerScreenSharing = false;
                    _tbScreenAudioVolume.Visible = false;
                    _lblScreenAudioVolume.Visible = false;
                    if (_lblScreenBadge.Visible && _lblScreenBadge.Text.Contains("демонстрац"))
                        _lblScreenBadge.Visible = false;
                }
            }
            else
            {
                // Камера выключена — плитка остаётся, показываем аватар.
                if (_tiles.TryGetValue(key, out var tile))
                {
                    tile.HasVideo = false;
                    tile.Pb.Visible = false;
                    var old = tile.Pb.Image; tile.Pb.Image = null; old?.Dispose();
                    tile.Panel.Invalidate();
                }
            }
        }

        private void OnTileFrame(string pid, string source, byte[] jpeg)
        {
            string key = TileKey(pid, source);
            if (!_tiles.TryGetValue(key, out var tile))
            {
                tile = EnsureTile(pid, _participants.TryGetValue(pid, out var nm) ? nm : pid, source);
                if (tile == null) return;
            }
            SetTileImage(tile, jpeg);
        }

        /// <summary>Оптимизация: декодируем JPEG в фоне (не на UI-потоке), на UI
        /// только присваиваем готовый Bitmap. Декод многих кадров на UI-потоке
        /// был основной причиной лагов в звонке.</summary>
        private void OnTileFrameOffThread(string pid, string source, byte[] jpeg)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap img;
                try { using var ms = new MemoryStream(jpeg); img = new Bitmap(ms); }
                catch { return; }
                if (IsDisposed || !IsHandleCreated) { img.Dispose(); return; }
                try { BeginInvoke(new Action(() => AssignTileImage(pid, source, img))); }
                catch { img.Dispose(); }
            });
        }

        private void AssignTileImage(string pid, string source, Bitmap img)
        {
            if (IsDisposed) { img.Dispose(); return; }
            string key = TileKey(pid, source);
            if (!_tiles.TryGetValue(key, out var tile))
            {
                tile = EnsureTile(pid, _participants.TryGetValue(pid, out var nm) ? nm : pid, source);
                if (tile == null) { img.Dispose(); return; }
            }
            var old = tile.Pb.Image;
            tile.Pb.Image = img;
            tile.HasVideo = true;
            if (!tile.Pb.Visible) tile.Pb.Visible = true;
            old?.Dispose();
        }

        /// <summary>Кадр своей камеры (из LocalCameraFrameReceived).</summary>
        private void OnSelfCameraFrame(byte[] jpeg)
        {
            // Пока открыто окно превью «Включить камеру» — кадры идут туда.
            if (_cameraPreviewForm != null)
            {
                try { _cameraPreviewForm.UpdateFrame(jpeg); } catch { }
                return;
            }
            // Камера выключена/отменена — игнорируем «догоняющие» кадры превью,
            // иначе после «Отмена» они бы создали плитку и камера казалась включённой.
            if (_cameraOff) return;
            if (_tilesHost == null) return;
            if (!_tiles.TryGetValue(TileKey(SelfPid, "camera"), out var tile))
                tile = AddParticipantTile(SelfPid, "Вы");
            if (tile != null) SetTileImage(tile, jpeg);
        }

        private void OnSelfCameraStopped()
        {
            if (_tiles.TryGetValue(TileKey(SelfPid, "camera"), out var tile))
            {
                tile.HasVideo = false;
                tile.Pb.Visible = false;
                var old = tile.Pb.Image; tile.Pb.Image = null; old?.Dispose();
                tile.Panel.Invalidate();
            }
        }

        private void SetTileImage(CallTile tile, byte[] jpeg)
        {
            Bitmap img;
            try { using var ms = new MemoryStream(jpeg); img = new Bitmap(ms); }
            catch { return; }
            var old = tile.Pb.Image;
            tile.Pb.Image = img;
            tile.HasVideo = true;
            if (!tile.Pb.Visible) tile.Pb.Visible = true;
            old?.Dispose();
        }

        /// <summary>Участник сменил имя в звонке — обновляем подписи его плиток.</summary>
        private void OnParticipantRenamed(string pid, string name)
        {
            if (string.IsNullOrEmpty(pid) || string.IsNullOrWhiteSpace(name)) return;
            _participants[pid] = name;
            foreach (var kv in _tiles)
                if (kv.Value.Pid == pid)
                {
                    kv.Value.Name = name;
                    if (kv.Value.Lbl != null)
                        kv.Value.Lbl.Text = (kv.Value.Source == "screen" ? "🖥 " : "") + name;
                }
        }

        /// <summary>Применить изменения своего профиля (имя/аватар) прямо в звонке:
        /// рассылаем новое имя участникам и перерисовываем свою плитку.</summary>
        public void ApplyMyProfileChanged()
        {
            try { _transport?.SetDisplayName(UserSession.UserName); } catch { }
            try
            {
                AvatarStore.Invalidate(UserSession.EffectiveId);
                AvatarStore.EnsureLoaded(UserSession.EffectiveId);
                foreach (var kv in _tiles)
                    if (kv.Value.Pid == SelfPid) kv.Value.Panel.Invalidate();
            }
            catch { }
        }

        /// <summary>Чужой пользователь обновил профиль (WS) — сбросить кэш аватара
        /// и перерисовать его плитки.</summary>
        public void OnRemoteProfileUpdated(int uid)
        {
            try
            {
                AvatarStore.Invalidate(uid);
                AvatarStore.EnsureLoaded(uid);
                string pid = uid.ToString();
                foreach (var kv in _tiles)
                    if (kv.Value.Pid == pid && !kv.Value.HasVideo) kv.Value.Panel.Invalidate();
            }
            catch { }
        }

        private void OnActiveSpeakers(string pidsJson)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(pidsJson);
                if (arr != null)
                {
                    // Каждое попадание в список говорящих продлевает подсветку на HOLD.
                    var until = DateTime.UtcNow.AddMilliseconds(SpeakHoldMs);
                    foreach (var p in arr) _speakUntil[p] = until;
                }
            }
            catch { }

            EnsureSpeakHoldTimer();
            RefreshSpeakingState();
        }

        private void EnsureSpeakHoldTimer()
        {
            if (_speakHoldTimer != null) return;
            _speakHoldTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _speakHoldTimer.Tick += (s, e) => RefreshSpeakingState();
            _speakHoldTimer.Start();
        }

        // Пересчитывает подсветку плиток с учётом удержания (hold).
        private void RefreshSpeakingState()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _tiles)
            {
                var tile = kv.Value;
                bool sp = tile.Source == "camera"
                    && _speakUntil.TryGetValue(tile.Pid, out var until)
                    && now < until;
                if (sp != tile.Speaking)
                {
                    tile.Speaking = sp;
                    try { tile.Panel.Invalidate(); } catch { }
                }
            }
        }

        // ── Отрисовка плитки (аватар + рамка говорящего) ────────────────
        private void PaintTile(Graphics g, CallTile tile)
        {
            var p = tile.Panel;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (!tile.HasVideo)
            {
                int d = Math.Min(p.Width, p.Height) / 3;
                if (d > 12)
                {
                    int x = (p.Width - d) / 2, y = (p.Height - d) / 2 - 6;
                    // Реальная аватарка, если есть; иначе цветной круг с буквой.
                    bool drawn = int.TryParse(tile.Pid, out int uid) && AvatarStore.DrawAvatar(g, uid, x, y, d);
                    if (!drawn)
                    {
                        using var br = new SolidBrush(AvatarColorFor(tile.Pid));
                        g.FillEllipse(br, x, y, d, d);
                        string letter = !string.IsNullOrEmpty(tile.Name) ? tile.Name.Substring(0, 1).ToUpper() : "?";
                        using var f = new Font("Segoe UI Black", d * 0.4f, FontStyle.Bold);
                        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(letter, f, Brushes.White, new RectangleF(x, y, d, d), sf);
                    }
                }
            }

            if (tile.Speaking)
            {
                using var pen = new Pen(Color.FromArgb(59, 165, 93), 3);
                g.DrawRectangle(pen, 1, 1, p.Width - 3, p.Height - 3);
            }
        }

        private static Color AvatarColorFor(string pid)
        {
            int h = 0; foreach (char c in pid ?? "") h = (h * 31 + c) & 0x7fffffff;
            Color[] palette =
            {
                Color.FromArgb(88,101,242), Color.FromArgb(235,69,158), Color.FromArgb(59,165,93),
                Color.FromArgb(250,166,26), Color.FromArgb(0,176,244), Color.FromArgb(156,89,182),
            };
            return palette[h % palette.Length];
        }

        // ── Полноэкранная плитка (демка во весь экран) ──────────────────
        private void ToggleFullscreen(string key)
        {
            // Уже в «театре» по этому ключу — выходим.
            if (_theaterKey == key) { ExitTheaterMode(); return; }

            int bar = key?.IndexOf('|') ?? -1;
            string src = bar >= 0 ? key.Substring(bar + 1) : "";
            string pid = bar >= 0 ? key.Substring(0, bar) : key;

            // Демонстрация экрана — нативный «театр» (плавные 60fps через WebView).
            if (src == "screen" && _tiles.ContainsKey(key))
            {
                _fullscreenKey = null;
                _theaterKey = key;
                var b = _tilesHost.Bounds;
                try { _tilesHost.Visible = false; } catch { }
                try { _transport?.EnterTheater(pid, "screen", b); } catch { }
                return;
            }

            // Прочее (камера) — прежний PictureBox-фуллскрин.
            if (_fullscreenKey == key) _fullscreenKey = null;
            else if (_tiles.ContainsKey(key)) _fullscreenKey = key;
            LayoutTiles();
        }

        /// <summary>Область для нативного видео: весь монитор (fullscreen) либо участок плиток.</summary>
        private Rectangle TheaterBounds() => _theaterFullscreen ? ClientRectangle : _tilesHost.Bounds;

        /// <summary>Развернуть/свернуть театр на весь монитор (нативное качество сохраняется).</summary>
        private void ToggleTheaterFullscreen()
        {
            if (_theaterKey == null) return;
            _theaterFullscreen = !_theaterFullscreen;

            if (_theaterFullscreen)
            {
                _savedBorder = FormBorderStyle;
                _savedWinState = WindowState;
                _savedBounds = Bounds;
                try
                {
                    WindowState = FormWindowState.Normal;   // чтобы можно было задать Bounds
                    FormBorderStyle = FormBorderStyle.None;
                    Bounds = Screen.FromHandle(Handle).Bounds; // весь монитор, включая taskbar
                }
                catch { }
            }
            else
            {
                try
                {
                    FormBorderStyle = _savedBorder;
                    Bounds = _savedBounds;
                    WindowState = _savedWinState;
                }
                catch { }
            }
            try { _transport?.UpdateTheaterBounds(TheaterBounds()); } catch { }
        }

        /// <summary>Выйти из нативного «театра» и вернуть плитки.</summary>
        private void ExitTheaterMode()
        {
            if (_theaterKey == null) return;
            // Если были в полноэкранном режиме — сперва вернём обычное окно.
            if (_theaterFullscreen) ToggleTheaterFullscreen();
            _theaterKey = null;
            try { _transport?.ExitTheater(); } catch { }
            try { if (_tilesHost != null) _tilesHost.Visible = true; } catch { }
            LayoutTiles();
        }

        // ── Индивидуальная громкость/мьют участника (правый клик) ────────
        private void ShowParticipantAudioMenu(string pid)
        {
            if (_userAudioPopup != null && !_userAudioPopup.IsDisposed)
            { try { _userAudioPopup.Close(); } catch { } _userAudioPopup = null; }

            string name = _participants.TryGetValue(pid, out var nm) ? nm : pid;
            float vol = _userVol.TryGetValue(pid, out var v) ? v : 1.0f;
            bool muted = _userMuted.TryGetValue(pid, out var m) && m;

            _userAudioPopup = new Form
            {
                Text = name,
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(40, 42, 46),
                ClientSize = new Size(240, 110),
                Location = Cursor.Position
            };
            var lbl = new Label { Text = "🔊 Громкость: " + name, ForeColor = Color.White, AutoSize = true, Location = new Point(12, 10), Font = new Font("Segoe UI", 9f) };
            var tb = new TrackBar { Minimum = 0, Maximum = 200, Value = (int)(vol * 100), TickStyle = TickStyle.None, Location = new Point(8, 32), Size = new Size(224, 40) };
            tb.ValueChanged += (s, e) => { _userVol[pid] = tb.Value / 100f; try { _transport?.SetParticipantVolume(pid, _userVol[pid]); } catch { } UserAudioPrefs.SetVolume(pid, _userVol[pid]); };
            var chk = new CheckBox { Text = "🔇 Заглушить", ForeColor = Color.White, AutoSize = true, Location = new Point(12, 80), Checked = muted, Font = new Font("Segoe UI", 9.5f) };
            chk.CheckedChanged += (s, e) => { _userMuted[pid] = chk.Checked; try { _transport?.SetParticipantMuted(pid, chk.Checked); } catch { } UserAudioPrefs.SetMuted(pid, chk.Checked); };
            _userAudioPopup.Controls.AddRange(new Control[] { lbl, tb, chk });
            _userAudioPopup.Deactivate += (s, e) => { try { _userAudioPopup?.Close(); } catch { } };
            _userAudioPopup.FormClosed += (s, e) => _userAudioPopup = null;
            _userAudioPopup.Show(this);
        }

        // ── Раскладка сетки ─────────────────────────────────────────────
        private void LayoutTiles()
        {
            if (_tilesHost == null) return;
            int n = _tileOrder.Count;
            if (n == 0) return;

            int w = _tilesHost.ClientSize.Width;
            int h = _tilesHost.ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            // Режим «на весь экран»: показываем только выбранную плитку.
            if (_fullscreenKey != null && _tiles.ContainsKey(_fullscreenKey))
            {
                foreach (var k in _tileOrder)
                {
                    if (!_tiles.TryGetValue(k, out var t)) continue;
                    if (k == _fullscreenKey)
                    {
                        t.Panel.Visible = true;
                        t.Panel.SetBounds(0, 0, w, h);
                        t.Panel.Invalidate();
                    }
                    else t.Panel.Visible = false;
                }
                return;
            }

            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            int rows = (int)Math.Ceiling((double)n / cols);
            const int gap = 6;
            int cellW = (w - gap * (cols + 1)) / cols;
            int cellH = (h - gap * (rows + 1)) / rows;
            if (cellW < 40 || cellH < 30) { cellW = Math.Max(40, cellW); cellH = Math.Max(30, cellH); }

            for (int i = 0; i < n; i++)
            {
                if (!_tiles.TryGetValue(_tileOrder[i], out var tile)) continue;
                int r = i / cols, c = i % cols;
                // Последний неполный ряд центрируем.
                int itemsInRow = (r == rows - 1) ? (n - r * cols) : cols;
                int rowWidth = itemsInRow * cellW + (itemsInRow - 1) * gap;
                int startX = (w - rowWidth) / 2;
                int x = startX + c * (cellW + gap);
                int y = gap + r * (cellH + gap);
                tile.Panel.Visible = true;
                tile.Panel.SetBounds(x, y, cellW, cellH);
                tile.Panel.Invalidate();
            }
        }
    }
}
