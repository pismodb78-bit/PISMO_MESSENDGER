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
        // Панель плитки с двойной буферизацией: обычный Panel при перерисовке
        // (рамка говорящего, значок мьюта, кадры) заметно мерцал аватаром.
        private sealed class TilePanel : Panel
        {
            public TilePanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint, true);
                UpdateStyles();
            }
        }

        private sealed class CallTile
        {
            public string Pid;
            public string Name;
            public string Source;      // "camera" | "screen"
            public Panel Panel;
            public PictureBox Pb;
            public Label Lbl;
            public Button WatchBtn;    // «▶ Смотреть стрим» (пока стрим не смотрим)
            public Button MenuBtn;     // «⋮» — полный экран / мини-окно / прекратить
            public GpuVideoSurface Gpu; // GPU-рендер демки (WPF/D3D); null = PictureBox
            public bool RawClickHooked; // клики демки (PIP/фуллскрин) уже повешены на Pb
            public bool Speaking;
            public bool HasVideo;
        }

        // Открыть театр сразу в полный экран после подключения к стриму.
        private bool _wantTheaterFullscreen;

        // ── «Смотреть стрим» (2.0): стрим не открывается сам — к нему подключаются ──
        private readonly Dictionary<string, string> _publishedStreams = new(); // pid -> имя стримера
        private readonly Dictionary<string, string> _watchIntent = new();      // pid -> "theater"|"popout"
        private readonly Dictionary<string, System.Windows.Forms.Timer> _watchTimeouts = new();
        // Кого зритель смотрел на момент завершения стрима (pid -> intent + когда).
        // При быстром перезапуске демки (смена кодека) авто-возобновляем просмотр,
        // чтобы не пришлось повторно жать «Смотреть стрим».
        private readonly Dictionary<string, (string intent, DateTime at)> _resumeWatch = new();

        // ── Стрим в отдельном окне (pop-out) ──
        private sealed class StreamPopout
        {
            public Form Form;
            public PictureBox Pb;
            public GpuVideoSurface Gpu;   // GPU-рендер стрима в отдельном окне
            public Label LblInfo;
        }
        private readonly Dictionary<string, StreamPopout> _streamPopouts = new();

        // ── Лента участников под театром (Discord-стиль) + шеврон «Скрыть участников» ──
        private Panel _theaterLane;          // узкая полоса с кнопкой-шевроном (вне WebView — airspace)
        private Button _btnStripToggle;
        private bool _stripVisible = true;   // лента миниатюр показана
        private int _stripCurH;              // текущая (анимируемая) высота ленты
        private const int LaneH = 28;
        private const int StripH = 112;

        private Panel _tilesHost;
        private readonly Dictionary<string, CallTile> _tiles = new();
        private readonly List<string> _tileOrder = new();           // порядок плиток
        private readonly Dictionary<string, string> _participants = new(); // pid -> name
        private string SelfPid => UserSession.EffectiveId.ToString();

        // Удержание подсветки говорящего: рамка гаснет не сразу, а через HOLD после
        // последнего звука — чтобы при чтении текста (с микропаузами) горела ровно.
        private readonly Dictionary<string, DateTime> _speakUntil = new();
        private System.Windows.Forms.Timer _speakHoldTimer;

        // Состояние мьютов участников для значков на плитке (🎤̶ / 🎧̶).
        private readonly HashSet<string> _micMutedPids = new();
        private readonly HashSet<string> _deafenedPids = new();

        private void OnParticipantMicMuted(string pid, bool muted)
        {
            if (string.IsNullOrEmpty(pid)) return;
            bool changed = muted ? _micMutedPids.Add(pid) : _micMutedPids.Remove(pid);
            if (changed) InvalidatePidTiles(pid);
        }

        private void OnParticipantDeafened(string pid, bool deaf)
        {
            if (string.IsNullOrEmpty(pid)) return;
            bool changed = deaf ? _deafenedPids.Add(pid) : _deafenedPids.Remove(pid);
            if (changed) InvalidatePidTiles(pid);
        }

        private void InvalidatePidTiles(string pid)
        {
            foreach (var kv in _tiles)
                if (kv.Value.Pid == pid) { try { kv.Value.Panel.Invalidate(); } catch { } }
        }
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
            // Участник вышел (в т.ч. аварийно) — гасим весь его стрим-контекст.
            _publishedStreams.Remove(pid);
            _watchIntent.Remove(pid);
            CancelWatchTimeout(pid);
            ClosePopout(pid);
            RemoveTile(TileKey(pid, "camera"));
            RemoveTile(TileKey(pid, "screen"));
            LayoutTiles();
            RefreshScreenPresence();
            if (existed && (DateTime.UtcNow - _tilesReadyAt).TotalMilliseconds > 1500)
                try { Sounds.UserLeft(); } catch { }
        }

        /// <summary>Гарантирует наличие плитки участника (камера-плитка = основная).</summary>
        private CallTile AddParticipantTile(string pid, string name)
        {
            return EnsureTile(pid, name, "camera");
        }

        // ── Своя демонстрация — плиткой в звонке ─────────────────────────
        /// <summary>Плитка собственной демки: превью идёт прямо в звонок; клик по
        /// плитке открывает привычное мини-окно (PIP). Закрыл/свернул PIP — превью
        /// продолжает жить в плитке (кадры в неё идут всегда).</summary>
        private void EnsureSelfScreenTile()
        {
            var tile = EnsureTile(SelfPid, "Вы", "screen");
            if (tile == null) return;
            if (!tile.HasVideo)
            {
                tile.HasVideo = true;
                tile.Lbl.Text = "🖥 Ваша демонстрация";
            }
            // Рендер через PictureBox (надёжно на любой системе). Двойной клик (на
            // весь участок) уже повешен в EnsureTile; здесь добавляем ТОЛЬКО
            // одиночный клик по своей демке — вынести превью в мини-окно.
            if (tile.Pb != null)
            {
                if (!tile.Pb.Visible) tile.Pb.Visible = true;
                if (!tile.RawClickHooked)
                {
                    tile.Pb.Click += (s, e) => ShowScreenSharePipContainer();
                    tile.RawClickHooked = true;
                    tile.Lbl?.BringToFront();
                }
            }
            LayoutTiles();
        }

        /// <summary>Сырой BGRA-кадр собственной демки: рендер в плитку и PIP.</summary>
        private void OnSelfScreenRawFrame(byte[] bgra, int w, int h)
        {
            if (_tiles.TryGetValue(TileKey(SelfPid, "screen"), out var tile))
            {
                // Плитка должна быть видима и «с видео» — иначе PaintTile рисует
                // заглушку-аватар поверх (панель серая). Форсируем как у зрителя.
                tile.HasVideo = true;
                if (tile.Pb != null && !tile.Pb.Visible) tile.Pb.Visible = true;
                if (tile.WatchBtn != null && tile.WatchBtn.Visible) tile.WatchBtn.Visible = false;
                SetTileRaw(tile, bgra, w, h);
            }
            if (_screenPipPicture != null && !_screenPipPicture.IsDisposed)
                SetPipRaw(bgra, w, h);
        }

        /// <summary>Старый BMP-путь превью (оставлен для совместимости контракта).</summary>
        private void OnSelfScreenFrame(byte[] imgBytes)
        {
            ShowScreenSharePip(imgBytes);   // мини-окно, если открыто (внутри сам проверит)
        }

        private void RemoveSelfScreenTile()
        {
            RemoveTile(TileKey(SelfPid, "screen"));
            LayoutTiles();
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

            tile.Panel = new TilePanel { BackColor = Color.FromArgb(47, 49, 54) };
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

            // Двойной клик — на весь экран; правый клик: на камере — громкость
            // голоса участника, на ДЕМКЕ — громкость именно ЭТОЙ демонстрации
            // (при двух демках в звонке каждую можно приглушить отдельно).
            void OnDouble(object s, EventArgs e) => ToggleFullscreen(capKey);
            void OnMouse(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || capPid == SelfPid) return;
                if (capSource == "screen") ShowScreenTileMenu(capPid);
                else ShowParticipantAudioMenu(capPid);
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
                    if (_rawBmp.TryGetValue(tile.Pb, out var rb)) { _rawBmp.Remove(tile.Pb); rb?.Dispose(); }
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

        // ── «Смотреть стрим»: объявление/подключение/отключение ─────────
        /// <summary>У участника начался стрим (или он уже шёл, когда мы вошли в
        /// звонок): показываем плитку с кнопкой подключения. Данные стрима при
        /// этом НЕ качаются — подписка случится только по нажатию.</summary>
        private void OnStreamPublished(string pid, string name)
        {
            if (string.IsNullOrEmpty(pid) || pid == SelfPid) return;
            if (string.IsNullOrWhiteSpace(name))
                name = _participants.TryGetValue(pid, out var nm) ? nm : pid;
            _publishedStreams[pid] = name;
            EnsureWatchTile(pid, name);
            RefreshScreenPresence();

            // Авто-возобновление: если только что смотрели этот стрим (демка
            // перезапустилась из-за смены кодека) — подключаемся сами, без клика.
            if (_resumeWatch.TryGetValue(pid, out var rw))
            {
                _resumeWatch.Remove(pid);
                if ((DateTime.UtcNow - rw.at).TotalSeconds < 15)
                {
                    WatchStream(pid, rw.intent);
                    return;   // звук «начался стрим» не нужен — просмотр не прерывался
                }
            }

            if ((DateTime.UtcNow - _tilesReadyAt).TotalMilliseconds > 1500)
                try { Sounds.ScreenOn(); } catch { }
        }

        /// <summary>Стрим участника завершён: убираем плитку/поп-аут/театр.</summary>
        private void OnStreamUnpublished(string pid)
        {
            if (string.IsNullOrEmpty(pid)) return;
            _publishedStreams.Remove(pid);
            // Если смотрели этот стрим — запомним, чтобы авто-возобновить при
            // быстром перезапуске демки (смена кодека), без ручного «Смотреть».
            if (_watchIntent.TryGetValue(pid, out var wi))
                _resumeWatch[pid] = (wi, DateTime.UtcNow);
            _watchIntent.Remove(pid);
            CancelWatchTimeout(pid);
            ClosePopout(pid);
            RemoveTile(TileKey(pid, "screen"));   // выйдет из театра, если смотрели
            LayoutTiles();
            RefreshScreenPresence();
        }

        /// <summary>Плитка-приглашение с кнопкой «▶ Смотреть стрим» (идемпотентно).</summary>
        private void EnsureWatchTile(string pid, string name)
        {
            var tile = EnsureTile(pid, name, "screen");
            if (tile == null) return;
            if (tile.WatchBtn == null)
            {
                var btn = new Button
                {
                    Text = "▶  Смотреть стрим",
                    Size = new Size(180, 42),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(88, 101, 242),   // фирменный blurple, как в Discord
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(108, 120, 248);
                btn.Resize += (s, e) =>
                {
                    try
                    {
                        btn.Region = System.Drawing.Region.FromHrgn(
                            NativeMethods.CreateRoundRectRgn(0, 0, btn.Width, btn.Height, 12, 12));
                    }
                    catch { }
                };
                string capPid = pid;
                btn.Click += (s, e) => WatchStream(capPid, "theater");
                btn.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowScreenTileMenu(capPid); };
                tile.WatchBtn = btn;
                tile.Panel.Controls.Add(btn);
                btn.BringToFront();
                var capTile = tile;
                tile.Panel.Resize += (s, e) => CenterWatchBtn(capTile);
                CenterWatchBtn(tile);
            }
            if (tile.MenuBtn == null)
            {
                // «⋮» в правом верхнем углу плитки: полный экран / мини-окно / прекратить.
                var mb = new Button
                {
                    Text = "⋮",
                    Size = new Size(28, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(120, 32, 34, 38),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                mb.FlatAppearance.BorderSize = 0;
                mb.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 68, 75);
                string capPid2 = pid;
                mb.Click += (s, e) => ShowWatchOptionsMenu(capPid2, mb);
                tile.MenuBtn = mb;
                tile.Panel.Controls.Add(mb);
                mb.BringToFront();
                var capTile2 = tile;
                void PlaceMenuBtn(object s, EventArgs e)
                {
                    try { capTile2.MenuBtn.Location = new Point(capTile2.Panel.Width - capTile2.MenuBtn.Width - 6, 6); }
                    catch { }
                }
                tile.Panel.Resize += PlaceMenuBtn;
                PlaceMenuBtn(null, null);
            }
            if (!tile.HasVideo)
            {
                tile.WatchBtn.Visible = true;
                tile.WatchBtn.Enabled = true;
                tile.WatchBtn.Text = "▶  Смотреть стрим";
            }
            LayoutTiles();
        }

        private static void CenterWatchBtn(CallTile tile)
        {
            try
            {
                if (tile?.WatchBtn == null || tile.Panel == null) return;
                // В узкой ленте миниатюр большая кнопка не влезает — ужимаем.
                bool tiny = tile.Panel.Width < 210 || tile.Panel.Height < 80;
                tile.WatchBtn.Size = tiny ? new Size(Math.Max(60, tile.Panel.Width - 16), 26) : new Size(180, 42);
                tile.WatchBtn.Font = new Font("Segoe UI Semibold", tiny ? 7.5f : 10f, FontStyle.Bold);
                tile.WatchBtn.Location = new Point(
                    (tile.Panel.Width - tile.WatchBtn.Width) / 2,
                    (tile.Panel.Height - tile.WatchBtn.Height) / 2 - 6);
            }
            catch { }
        }

        /// <summary>Подключиться к идущему стриму (с живой точки — не «с начала»).
        /// intent: "theater" — открыть на весь видео-участок; "popout" — кадры в
        /// отдельное окно, театр не трогаем.</summary>
        private void WatchStream(string pid, string intent)
        {
            if (string.IsNullOrEmpty(pid) || pid == SelfPid) return;
            _watchIntent[pid] = intent;
            if (_tiles.TryGetValue(TileKey(pid, "screen"), out var t) && t.WatchBtn != null)
            {
                t.WatchBtn.Enabled = false;
                t.WatchBtn.Text = "Подключение…";
            }
            try { _transport?.WatchScreen(pid); } catch { }
            ArmWatchTimeout(pid);
        }

        /// <summary>Полностью перестать смотреть стрим (театр/поп-аут/подписка).</summary>
        private void StopWatching(string pid)
        {
            string key = TileKey(pid, "screen");
            if (_theaterKey == key) ExitTheaterMode();   // сам отпишется, если нет поп-аута
            ClosePopout(pid);
            _watchIntent.Remove(pid);
            try { _transport?.UnwatchScreen(pid); } catch { }
        }

        // Сторож подключения с АВТО-ПЕРЕПОДКЛЮЧЕНИЕМ: если кадры так и не пошли
        // (сервер поставил трек на паузу после перезапуска демки / смены кодека),
        // каждые 3 c пере-подписываемся (unwatch+watch) — это «будит» сервер и он
        // возобновляет отправку. Так до 15 c; если и тогда пусто — возвращаем кнопку.
        private void ArmWatchTimeout(string pid)
        {
            CancelWatchTimeout(pid);
            var start = DateTime.UtcNow;
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (s, e) =>
            {
                // Кадры пошли — успех, сторож больше не нужен.
                if (_tiles.TryGetValue(TileKey(pid, "screen"), out var tile) && tile.HasVideo)
                { CancelWatchTimeout(pid); return; }
                // Пользователь уже не хочет смотреть — прекращаем.
                if (!_watchIntent.ContainsKey(pid)) { CancelWatchTimeout(pid); return; }
                // 15 c без картинки — сдаёмся, откатываем кнопку.
                if ((DateTime.UtcNow - start).TotalSeconds >= 15)
                { CancelWatchTimeout(pid); OnWatchFailed(pid, "стрим не отвечает (таймаут)"); return; }
                // Ещё есть время — ПЕРЕ-ПОДПИСЫВАЕМСЯ (SetSubscribed(true) внутри
                // WatchScreen), БЕЗ отписки: ничего не рвём, просто «будим» сервер,
                // чтобы он возобновил паузнутый трек. Срабатывает только пока
                // картинки нет — с приходом кадров таймер гасится (OnTileStarted).
                try { _transport?.WatchScreen(pid); } catch { }
            };
            _watchTimeouts[pid] = t;
            t.Start();
        }

        private void CancelWatchTimeout(string pid)
        {
            if (_watchTimeouts.TryGetValue(pid, out var t))
            {
                try { t.Stop(); t.Dispose(); } catch { }
                _watchTimeouts.Remove(pid);
            }
        }

        /// <summary>Подключение к стриму не удалось (участник вышел, стрим завершён,
        /// таймаут) — откатываем кнопку и сообщаем причину.</summary>
        private void OnWatchFailed(string pid, string err)
        {
            CancelWatchTimeout(pid);
            _watchIntent.Remove(pid);
            try { _transport?.UnwatchScreen(pid); } catch { }
            if (_tiles.TryGetValue(TileKey(pid, "screen"), out var t) && t.WatchBtn != null && !t.HasVideo)
            {
                t.WatchBtn.Enabled = true;
                t.WatchBtn.Text = "▶  Смотреть стрим";
            }
            ClosePopout(pid);
            try { _lblStatus.Text = "Не удалось подключиться к стриму: " + err; } catch { }
        }

        /// <summary>Бейдж «идёт демонстрация» — по факту ОПУБЛИКОВАННЫХ стримов;
        /// ползунок громкости демки — только когда реально что-то смотрим.</summary>
        private void RefreshScreenPresence()
        {
            bool anyWatching = false;
            foreach (var kv in _tiles)
                if (kv.Value.Source == "screen" && kv.Value.HasVideo
                    && kv.Value.Pid != SelfPid) { anyWatching = true; break; }   // своя демка — не «просмотр»
            _peerScreenSharing = anyWatching;
            _tbScreenAudioVolume.Visible = anyWatching;
            _lblScreenAudioVolume.Visible = anyWatching;

            // Свою демонстрацию бейджит отдельно (OnLocalScreenStarted) — не трогаем.
            if (_screenSharing) return;

            if (_publishedStreams.Count > 0)
            {
                // ВАЖНО: именуем, КТО демонстрирует, иначе безымянное «Идёт
                // демонстрация экрана» выглядит так, будто стримишь ты сам.
                string who = null;
                foreach (var v in _publishedStreams.Values) { who = v; break; }
                _lblScreenBadge.Text = _publishedStreams.Count == 1
                    ? "🖥 " + who + " демонстрирует экран"
                    : "🖥 Демонстрируют экран: " + _publishedStreams.Count;
                _lblScreenBadge.Visible = true;
            }
            else if (_lblScreenBadge.Visible && _lblScreenBadge.Text.Contains("демонстр"))
                _lblScreenBadge.Visible = false;
        }

        // ── События треков ──────────────────────────────────────────────
        private void OnTileStarted(string pid, string name, string source)
        {
            if (pid == SelfPid && source == "camera") return; // своя камера — отдельный поток кадров
            var tile = EnsureTile(pid, name, source);
            if (tile != null) tile.HasVideo = true;

            if (source == "screen")
            {
                CancelWatchTimeout(pid);
                if (tile?.WatchBtn != null) tile.WatchBtn.Visible = false;
                RefreshScreenPresence();
                // Нативный путь (без WebView): демка показывается обычной плиткой
                // в сетке; на весь экран — двойным кликом (PictureBox-фуллскрин).
                // Прежний нативный «театр» 60fps через WebView здесь недоступен.
            }
        }

        private void OnTileStopped(string pid, string source)
        {
            string key = TileKey(pid, source);
            if (source == "screen")
            {
                if (_publishedStreams.ContainsKey(pid) && _tiles.TryGetValue(key, out var wt))
                {
                    // Мы отписались, но стрим ещё идёт — плитка возвращается к кнопке.
                    if (_theaterKey == key) ExitTheaterMode();
                    wt.HasVideo = false;
                    wt.Pb.Visible = false;
                    var old = wt.Pb.Image; wt.Pb.Image = null; old?.Dispose();
                    // GPU-поверхность убираем, иначе она перекроет кнопку «Смотреть стрим».
                    if (wt.Gpu != null) { try { wt.Panel.Controls.Remove(wt.Gpu); wt.Gpu.Dispose(); } catch { } wt.Gpu = null; }
                    EnsureWatchTile(pid, wt.Name);
                    wt.Panel.Invalidate();
                }
                else
                {
                    RemoveTile(key);
                    LayoutTiles();
                }
                RefreshScreenPresence();
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
            if (tile.WatchBtn != null && tile.WatchBtn.Visible) tile.WatchBtn.Visible = false;
            old?.Dispose();

            // Стрим открыт в отдельном окне — дублируем кадр туда (своя копия
            // Bitmap: делить один GDI-объект между двумя PictureBox нельзя —
            // Dispose одного уронит отрисовку другого).
            if (source == "screen" && _streamPopouts.TryGetValue(pid, out var po)
                && po.Form != null && !po.Form.IsDisposed)
            {
                try
                {
                    var copy = (Bitmap)img.Clone();
                    var oldP = po.Pb.Image;
                    po.Pb.Image = copy;
                    oldP?.Dispose();
                    if (po.LblInfo != null && po.LblInfo.Visible) po.LblInfo.Visible = false;
                }
                catch { }
            }
        }

        // ── GPU-рендер входящей демки (сырой BGRA → WPF/D3D-поверхность) ──
        /// <summary>Кадр демки: рендер на видеокарте (WriteableBitmap → композиция
        /// DWM/D3D). Масштабирование в плитке/фуллскрине/попауте — на GPU, CPU не
        /// перекодирует кадры в BMP и не скейлит их в каждом Paint.</summary>
        private void OnTileRawFrame(string pid, string source, byte[] bgra, int w, int h)
        {
            string key = TileKey(pid, source);
            if (!_tiles.TryGetValue(key, out var tile))
            {
                tile = EnsureTile(pid, _participants.TryGetValue(pid, out var nm) ? nm : pid, source);
                if (tile == null) return;
            }

            // Рендер демки через PictureBox (GPU-поверхность серела на части систем).
            // Двойной клик/правый клик уже повешены в EnsureTile — здесь только
            // делаем Pb видимым (EnsureTile создаёт его скрытым).
            if (!tile.Pb.Visible) tile.Pb.Visible = true;
            tile.HasVideo = true;
            if (tile.WatchBtn != null && tile.WatchBtn.Visible) tile.WatchBtn.Visible = false;
            SetTileRaw(tile, bgra, w, h);

            // Стрим открыт в отдельном окне — тот же кадр в его PictureBox.
            if (source == "screen" && _streamPopouts.TryGetValue(pid, out var po)
                && po.Form != null && !po.Form.IsDisposed && po.Pb != null && !po.Pb.IsDisposed)
            {
                try
                {
                    SetPictureRaw(po.Pb, bgra, w, h);
                    if (po.LblInfo != null && po.LblInfo.Visible) po.LblInfo.Visible = false;
                }
                catch { }
            }
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

        /// <summary>Показ сырого BGRA-кадра демки через PictureBox (как камера).
        /// GPU-поверхность (ElementHost/WPF) на части систем не композитится и
        /// висит серым прямоугольником — PictureBox рисует надёжно.</summary>
        private void SetTileRaw(CallTile tile, byte[] bgra, int w, int h)
        {
            if (tile == null || tile.Pb == null || tile.Pb.IsDisposed) return;
            SetPictureRaw(tile.Pb, bgra, w, h);
            tile.HasVideo = true;
        }

        private void SetPipRaw(byte[] bgra, int w, int h)
        {
            if (_screenPipPicture == null || _screenPipPicture.IsDisposed) return;
            if (!_screenPipPicture.Visible) _screenPipPicture.Visible = true;
            SetPictureRaw(_screenPipPicture, bgra, w, h);
        }

        // Переиспользуемые битмапы демки (по одному на PictureBox): new Bitmap на
        // каждый кадр 1080p60 — ~480МБ/с мусора и рывки GC. Пишем в тот же битмап
        // и перерисовываем.
        private readonly Dictionary<PictureBox, Bitmap> _rawBmp = new();

        /// <summary>Копирует BGRA-буфер в НОВЫЙ Bitmap и показывает в PictureBox.
        /// ВАЖНО: формат Format32bppRgb (без альфы), а НЕ Argb. Сырой кадр WGC —
        /// B8G8R8A8, где альфа-байт = 0 (захват не заполняет альфу). В 32bppArgb
        /// альфа=0 = ПОЛНОСТЬЮ ПРОЗРАЧНЫЙ пиксель → плитка своей демки рисовалась
        /// чёрной (фон PictureBox), хотя байты RGB непустые (яркость на бейдже была
        /// нормальной). У зрителя кадр декодируется из видео с альфой=255, поэтому
        /// там всё показывалось. 32bppRgb игнорирует альфу — кадр непрозрачен.</summary>
        private void SetPictureRaw(PictureBox pb, byte[] bgra, int w, int h)
        {
            if (pb == null || pb.IsDisposed || bgra == null || w <= 0 || h <= 0) return;
            Bitmap img;
            try
            {
                img = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var bd = img.LockBits(new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    int stride = bd.Stride;
                    if (stride == w * 4)
                        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bd.Scan0, Math.Min(bgra.Length, w * 4 * h));
                    else
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(bgra, y * w * 4,
                                IntPtr.Add(bd.Scan0, y * stride), w * 4);
                }
                finally { img.UnlockBits(bd); }
            }
            catch { return; }
            var old = pb.Image;
            pb.Image = img;
            if (!pb.Visible) pb.Visible = true;
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

        private readonly HashSet<string> _activeSpeakers = new();   // кто говорит ПРЯМО СЕЙЧАС

        private void OnActiveSpeakers(string pidsJson)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<string[]>(pidsJson) ?? Array.Empty<string>();
                var now = DateTime.UtcNow;
                var next = new HashSet<string>(arr);
                // Кто ПЕРЕСТАЛ говорить (был в наборе, теперь нет) — даём короткий
                // «хвост» затухания, чтобы рамка не дёргалась между обновлениями.
                foreach (var p in _activeSpeakers)
                    if (!next.Contains(p)) _speakUntil[p] = now.AddMilliseconds(SpeakHoldMs);
                // Кто говорит сейчас — держим рамку без таймера (LiveKit шлёт событие
                // только на ИЗМЕНЕНИЯ: пока человек говорит, новых событий нет).
                foreach (var p in next) _speakUntil.Remove(p);
                _activeSpeakers.Clear();
                foreach (var p in next) _activeSpeakers.Add(p);
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

        // Пересчитывает подсветку плиток: говорит СЕЙЧАС (в наборе active) или в
        // пределах короткого хвоста затухания после прекращения.
        private void RefreshSpeakingState()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _tiles)
            {
                var tile = kv.Value;
                bool sp = tile.Source != "screen"
                    && !_micMutedPids.Contains(tile.Pid)      // замьюченный не «говорит»
                    && (_activeSpeakers.Contains(tile.Pid)
                        || (_speakUntil.TryGetValue(tile.Pid, out var until) && now < until));
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

            // Значок мьюта в левом нижнем углу (демка — без значка): наушники,
            // если участник заглушил всё (deafen), иначе перечёркнутый микрофон.
            // Для СВОЕЙ плитки берём локальное состояние (как в Discord).
            if (tile.Source != "screen")
            {
                bool isSelf = tile.Pid == SelfPid;
                bool deaf = isSelf ? _remoteAllMuted : _deafenedPids.Contains(tile.Pid);
                bool mic = isSelf ? _muted : _micMutedPids.Contains(tile.Pid);
                // Значок рисуем НАД полоской имени (Lbl докнут снизу), иначе она
                // перекрывала нижнюю половину значка.
                if (deaf || mic) DrawMuteBadge(g, p, deaf, tile.Lbl?.Height ?? 0);
            }
        }

        // Discord-подобный значок: тёмный скруглённый чип с перечёркнутой иконкой
        // (микрофон или наушники) в левом нижнем углу плитки.
        private static void DrawMuteBadge(Graphics g, Panel p, bool deaf, int bottomOffset = 0)
        {
            int s = Math.Max(26, Math.Min(p.Width, p.Height) / 7);
            int pad = 8;
            var chipBg = Color.FromArgb(230, 24, 25, 28);
            var rect = new Rectangle(pad, p.Height - s - pad - bottomOffset, s, s);
            using (var bg = new SolidBrush(chipBg))
            using (var path = RoundRect(rect, 7))
                g.FillPath(bg, path);
            float ip = s * 0.20f;   // внутренний отступ иконки
            MuteGlyph.Draw(g, new RectangleF(rect.X + ip, rect.Y + ip, s - 2 * ip, s - 2 * ip),
                           deaf, Color.FromArgb(255, 24, 25, 28));
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
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

        /// <summary>Меню «⋮» на плитке стрима: полный экран / мини-окно / прекратить.</summary>
        private void ShowWatchOptionsMenu(string pid, Control anchor)
        {
            try
            {
                bool watching = _tiles.TryGetValue(TileKey(pid, "screen"), out var t) && t.HasVideo;
                var menu = new ContextMenuStrip
                {
                    BackColor = Color.FromArgb(24, 25, 28),
                    ForeColor = Color.FromArgb(220, 221, 222)
                };
                if (!watching)
                    menu.Items.Add("▶  Смотреть стрим", null, (s, e) => WatchStream(pid, "theater"));
                menu.Items.Add("⛶  Полный экран", null, (s, e) => WatchFullscreen(pid));
                menu.Items.Add("🗔  Мини-окно (отдельное окно)", null, (s, e) =>
                {
                    if (_tiles.TryGetValue(TileKey(pid, "screen"), out var t2) && t2.HasVideo)
                        OpenStreamPopout(pid);
                    else
                        WatchStream(pid, "popout");
                });
                if (watching)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    var stop = new ToolStripMenuItem("⏹  Прекратить просмотр")
                    { ForeColor = Color.FromArgb(240, 71, 71) };
                    stop.Click += (s, e) => StopWatching(pid);
                    menu.Items.Add(stop);
                }
                if (anchor != null) menu.Show(anchor, new Point(0, anchor.Height));
                else menu.Show(Cursor.Position);
            }
            catch { }
        }

        /// <summary>Развернуть демку участника на весь видео-участок окна звонка.
        /// Нативный путь: обычный PictureBox-фуллскрин (без WebView-«театра»).</summary>
        private void WatchFullscreen(string pid)
        {
            string key = TileKey(pid, "screen");
            if (_tiles.ContainsKey(key))
            {
                _fullscreenKey = key;
                LayoutTiles();
            }
        }

        // ── Контекстное меню плитки стрима (ПКМ) ────────────────────────
        private void ShowScreenTileMenu(string pid)
        {
            try
            {
                bool watching = _tiles.TryGetValue(TileKey(pid, "screen"), out var t) && t.HasVideo;
                var menu = new ContextMenuStrip();
                if (!watching)
                    menu.Items.Add("▶  Смотреть стрим", null, (s, e) => WatchStream(pid, "theater"));
                else
                    menu.Items.Add("⏹  Перестать смотреть", null, (s, e) => StopWatching(pid));
                menu.Items.Add("🗔  Стрим в отдельном окне", null, (s, e) => OpenStreamPopout(pid));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("🔊  Громкость демки…", null, (s, e) => ShowScreenAudioMenu(pid));
                menu.Show(Cursor.Position);
            }
            catch { }
        }

        // ── Полноэкранная плитка (демка во весь экран) ──────────────────
        private void ToggleFullscreen(string key)
        {
            // Уже в «театре» по этому ключу — выходим.
            if (_theaterKey == key) { ExitTheaterMode(); return; }

            // Нативный путь (без WebView): и демка, и камера разворачиваются
            // обычным PictureBox-фуллскрином внутри сетки плиток.
            if (_fullscreenKey == key) _fullscreenKey = null;
            else if (_tiles.ContainsKey(key)) _fullscreenKey = key;
            LayoutTiles();
        }

        /// <summary>Войти в «театр»: видео — нативно на весь участок, остальные
        /// участники — лентой миниатюр снизу (Discord-стиль), над ней шеврон
        /// «Скрыть участников».</summary>
        private void EnterTheaterMode(string key)
        {
            int bar = key.IndexOf('|');
            string pid = bar > 0 ? key.Substring(0, bar) : key;
            _fullscreenKey = null;
            _theaterKey = key;
            BuildTheaterChrome();
            _stripCurH = _stripVisible ? StripH : 0;
            try { _tilesHost.Anchor = AnchorStyles.None; } catch { }  // раскладка ленты — вручную
            UpdateStripToggleText();
            LayoutTheaterChrome();
            try { _transport?.EnterTheater(pid, "screen", TheaterBounds()); } catch { }
            // Запрошен «полный экран» из меню «⋮» — разворачиваем сразу.
            if (_wantTheaterFullscreen)
            {
                _wantTheaterFullscreen = false;
                if (!_theaterFullscreen) ToggleTheaterFullscreen();
            }
        }

        /// <summary>Полоса с шевроном + подписка на ресайз формы (создаётся один раз).</summary>
        private void BuildTheaterChrome()
        {
            if (_theaterLane != null) return;
            _theaterLane = new Panel { BackColor = Color.FromArgb(20, 21, 23), Visible = false };
            _btnStripToggle = new Button
            {
                Size = new Size(200, 22),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.FromArgb(200, 202, 208),
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand,
                TabStop = false,
                Top = 3
            };
            _btnStripToggle.FlatAppearance.BorderSize = 0;
            _btnStripToggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 58, 64);
            _btnStripToggle.Click += (s, e) => ToggleStrip();
            new ToolTip().SetToolTip(_btnStripToggle, "Скрыть/показать участников");
            _theaterLane.Controls.Add(_btnStripToggle);
            Controls.Add(_theaterLane);
            Resize += (s, e) => { if (_theaterKey != null) LayoutTheaterChrome(); };
        }

        private void UpdateStripToggleText()
        {
            if (_btnStripToggle != null)
                _btnStripToggle.Text = _stripVisible ? "⌄  Скрыть участников" : "⌃  Показать участников";
        }

        /// <summary>Плавно скрыть/показать ленту участников под театром —
        /// видео при этом плавно занимает освободившееся место.</summary>
        private void ToggleStrip()
        {
            if (_theaterKey == null) return;
            _stripVisible = !_stripVisible;
            UpdateStripToggleText();
            int to = _stripVisible ? StripH : 0;
            Anim.Int(_btnStripToggle, _stripCurH, to, 240,
                v => { _stripCurH = v; LayoutTheaterChrome(); });
        }

        /// <summary>Раскладка театра: видео сверху, лента миниатюр и шеврон снизу.
        /// Шеврон живёт в СВОЕЙ полосе вне WebView — нативный контрол поверх
        /// WebView не отрисовывается (airspace).</summary>
        private void LayoutTheaterChrome()
        {
            if (_theaterKey == null || _theaterLane == null) return;
            if (_theaterFullscreen)
            {
                _theaterLane.Visible = false;
                _tilesHost.Visible = false;
                try { _transport?.UpdateTheaterBounds(TheaterBounds()); } catch { }
                return;
            }
            int w = ClientSize.Width;
            int bottom = ClientSize.Height - _pnlButtons.Height;
            int laneTop = bottom - LaneH - _stripCurH;
            _theaterLane.Visible = true;
            _theaterLane.SetBounds(0, laneTop, w, LaneH);
            _theaterLane.BringToFront();
            _btnStripToggle.Left = (w - _btnStripToggle.Width) / 2;
            _tilesHost.Visible = _stripCurH > 4;
            _tilesHost.SetBounds(0, laneTop + LaneH, w, Math.Max(1, _stripCurH));
            try { _transport?.UpdateTheaterBounds(TheaterBounds()); } catch { }
            if (_tilesHost.Visible) LayoutTiles();
        }

        /// <summary>Убрать ленту/шеврон и вернуть плиткам всю область.</summary>
        private void HideTheaterChrome()
        {
            Anim.Cancel(_btnStripToggle);
            if (_theaterLane != null) _theaterLane.Visible = false;
            _stripVisible = true;
            _stripCurH = StripH;
            UpdateStripToggleText();
            if (_tilesHost != null)
            {
                _tilesHost.Visible = true;
                _tilesHost.SetBounds(0, 56, ClientSize.Width,
                    Math.Max(50, ClientSize.Height - 56 - _pnlButtons.Height));
                _tilesHost.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            }
        }

        /// <summary>Область для нативного видео: весь монитор (fullscreen) либо
        /// участок над лентой участников (высота ленты анимируется).</summary>
        private Rectangle TheaterBounds()
        {
            if (_theaterFullscreen) return ClientRectangle;
            int bottom = ClientSize.Height - _pnlButtons.Height;
            int laneTop = bottom - LaneH - _stripCurH;
            return new Rectangle(0, 56, ClientSize.Width, Math.Max(50, laneTop - 56));
        }

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
            LayoutTheaterChrome();   // лента/шеврон: спрятать в fullscreen, вернуть при выходе
        }

        /// <summary>Выйти из нативного «театра» и вернуть плитки. Как в Discord:
        /// выход из просмотра = отписка от стрима (если он не открыт в поп-ауте) —
        /// плитка возвращается к кнопке «Смотреть стрим».</summary>
        private void ExitTheaterMode()
        {
            if (_theaterKey == null) return;
            string exitKey = _theaterKey;
            // Если были в полноэкранном режиме — сперва вернём обычное окно.
            if (_theaterFullscreen) ToggleTheaterFullscreen();
            _theaterKey = null;
            try { _transport?.ExitTheater(); } catch { }
            HideTheaterChrome();
            LayoutTiles();

            int bar = exitKey.IndexOf('|');
            string pid = bar > 0 ? exitKey.Substring(0, bar) : exitKey;
            bool hasPopout = _streamPopouts.TryGetValue(pid, out var po)
                             && po.Form != null && !po.Form.IsDisposed;
            if (!hasPopout && _publishedStreams.ContainsKey(pid))
            {
                _watchIntent.Remove(pid);
                try { _transport?.UnwatchScreen(pid); } catch { }
            }
        }

        // ── Стрим в отдельном окне (pop-out) ─────────────────────────────
        /// <summary>Открыть стрим участника в отдельном окне. Повторный вызов
        /// фокусирует существующее окно; окно, умершее некорректно (краш/
        /// невалидный хэндл), пересоздаётся заново — «неоткрывающихся» окон нет.</summary>
        private void OpenStreamPopout(string pid)
        {
            try
            {
                if (_streamPopouts.TryGetValue(pid, out var existing))
                {
                    if (existing.Form != null && !existing.Form.IsDisposed)
                    {
                        try
                        {
                            if (existing.Form.WindowState == FormWindowState.Minimized)
                                existing.Form.WindowState = FormWindowState.Normal;
                            existing.Form.BringToFront();
                            existing.Form.Activate();
                        }
                        catch { }
                        return;
                    }
                    _streamPopouts.Remove(pid);   // окно умерло некорректно — пересоздаём
                }

                string name = _participants.TryGetValue(pid, out var nm) ? nm
                            : (_publishedStreams.TryGetValue(pid, out var pn) ? pn : pid);

                var f = new Form
                {
                    Text = "🖥 Стрим — " + name,
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(900, 560),
                    MinimumSize = new Size(320, 220),
                    BackColor = Color.FromArgb(15, 16, 18),
                    KeyPreview = true,
                    ShowInTaskbar = true
                };

                var top = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.FromArgb(30, 31, 34) };
                var chkTop = new CheckBox
                {
                    Text = "📌 Поверх всех окон",
                    ForeColor = Color.FromArgb(200, 202, 208),
                    AutoSize = true,
                    Location = new Point(8, 4),
                    Font = new Font("Segoe UI", 8f)
                };
                chkTop.CheckedChanged += (s, e) => { try { f.TopMost = chkTop.Checked; } catch { } };
                top.Controls.Add(chkTop);

                var pb = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.FromArgb(15, 16, 18)
                };
                var lblInfo = new Label
                {
                    Text = "Подключение к стриму…",
                    Dock = DockStyle.Bottom,
                    Height = 24,
                    ForeColor = Color.FromArgb(150, 152, 158),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9f)
                };
                // Рендер через PictureBox (GPU-поверхность серела на части систем).
                f.Controls.Add(pb);
                f.Controls.Add(lblInfo);
                f.Controls.Add(top);
                pb.BringToFront();
                lblInfo.BringToFront();
                top.BringToFront();
                f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { try { f.Close(); } catch { } } };

                string capPid = pid;
                f.FormClosed += (s, e) => OnPopoutClosed(capPid);

                _streamPopouts[pid] = new StreamPopout { Form = f, Pb = pb, Gpu = null, LblInfo = lblInfo };
                f.Show();
                Anim.FadeIn(f);

                // Кадры чаще и крупнее — это окно и есть «экран» для зрителя.
                try { _transport?.SetTileRate(pid, "screen", 30, 1280, true); } catch { }

                // Ещё не смотрим стрим — подключаемся (театр не трогаем).
                bool watching = _tiles.TryGetValue(TileKey(pid, "screen"), out var t) && t.HasVideo;
                if (!watching) WatchStream(pid, "popout");
                else lblInfo.Visible = false;
            }
            catch { }
        }

        /// <summary>Окно поп-аута закрылось (любым способом, включая некорректный):
        /// снимаем ускоренную выкачку кадров и отписываемся от стрима, если его
        /// больше никто не смотрит (нет театра).</summary>
        private void OnPopoutClosed(string pid)
        {
            try
            {
                if (_streamPopouts.TryGetValue(pid, out var cpo) && cpo.Pb != null
                    && _rawBmp.TryGetValue(cpo.Pb, out var prb)) { _rawBmp.Remove(cpo.Pb); prb?.Dispose(); }
                _streamPopouts.Remove(pid);
                try { _transport?.SetTileRate(pid, "screen", 15, 960, false); } catch { }
                if (_theaterKey != TileKey(pid, "screen"))
                {
                    _watchIntent.Remove(pid);
                    try { _transport?.UnwatchScreen(pid); } catch { }
                }
            }
            catch { }
        }

        private void ClosePopout(string pid)
        {
            if (_streamPopouts.TryGetValue(pid, out var po))
            {
                try { if (po.Form != null && !po.Form.IsDisposed) po.Form.Close(); } catch { }
                _streamPopouts.Remove(pid);
            }
        }

        /// <summary>Закрыть все поп-ауты и сторожевые таймеры (выход из звонка).</summary>
        private void CloseAllStreamPopouts()
        {
            foreach (var pid in new List<string>(_streamPopouts.Keys)) ClosePopout(pid);
            foreach (var t in _watchTimeouts.Values) { try { t.Stop(); t.Dispose(); } catch { } }
            _watchTimeouts.Clear();
        }

        // ── Индивидуальная громкость КОНКРЕТНОЙ демонстрации (правый клик по
        //    её плитке): при нескольких демках каждую можно тише/громче отдельно ──
        private readonly Dictionary<string, float> _screenVol = new();

        private void ShowScreenAudioMenu(string pid)
        {
            string name = _participants.TryGetValue(pid, out var nm) ? nm : pid;
            float vol = _screenVol.TryGetValue(pid, out var v) ? v : 1.0f;
            ShowVolumeMenu("🖥 Громкость демки: " + name, vol, false, false,
                v2 => { _screenVol[pid] = v2; try { _transport?.SetScreenShareVolume(pid, v2); } catch { } },
                null);
        }

        // Всплывающее меню громкости прямо в окне (ContextMenuStrip со встроенным
        // ползунком): закрывается по клику вне, без отдельного окна и крестика.
        private void ShowVolumeMenu(string title, float vol, bool withMute, bool muted,
                                    Action<float> onVol, Action<bool> onMute)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.System
            };
            menu.Items.Add(new ToolStripLabel(title)
            { ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) });

            var tb = new TrackBar
            {
                Minimum = 0,
                Maximum = 300,
                Value = Math.Min(300, Math.Max(0, (int)(vol * 100))),
                TickStyle = TickStyle.None,
                Width = 236,
                Height = 40,
                BackColor = Color.FromArgb(40, 42, 46)
            };
            tb.ValueChanged += (s, e) => { try { onVol?.Invoke(tb.Value / 100f); } catch { } };
            menu.Items.Add(new ToolStripControlHost(tb)
            { AutoSize = false, Size = new Size(240, 42), Margin = new Padding(2, 2, 2, 2), BackColor = Color.FromArgb(40, 42, 46) });

            if (withMute)
            {
                var mute = new ToolStripMenuItem("🔇 Заглушить")
                { CheckOnClick = true, Checked = muted, ForeColor = Color.White };
                mute.CheckedChanged += (s, e) => { try { onMute?.Invoke(mute.Checked); } catch { } };
                menu.Items.Add(mute);
            }
            menu.Closed += (s, e) => { try { menu.Dispose(); } catch { } };
            menu.Show(Cursor.Position);
        }

        // ── Индивидуальная громкость/мьют участника (правый клик) ────────
        private void ShowParticipantAudioMenu(string pid)
        {
            string name = _participants.TryGetValue(pid, out var nm) ? nm : pid;
            float vol = _userVol.TryGetValue(pid, out var v) ? v
                      : (UserAudioPrefs.Has(pid) ? UserAudioPrefs.GetVolume(pid) : 1.0f);
            bool muted = _userMuted.TryGetValue(pid, out var m) ? m
                       : (UserAudioPrefs.Has(pid) && UserAudioPrefs.GetMuted(pid));

            ShowVolumeMenu("🔊 Громкость: " + name, vol, true, muted,
                v2 => { _userVol[pid] = v2; try { _transport?.SetParticipantVolume(pid, v2); } catch { } UserAudioPrefs.SetVolume(pid, v2); },
                mm => { _userMuted[pid] = mm; try { _transport?.SetParticipantMuted(pid, mm); } catch { } UserAudioPrefs.SetMuted(pid, mm); });
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

            // Театр: остальные участники — горизонтальная лента миниатюр внизу
            // (сам стрим рисуется нативно выше и в ленте не дублируется).
            if (_theaterKey != null)
            {
                var keys = new List<string>();
                foreach (var k in _tileOrder) if (k != _theaterKey) keys.Add(k);
                if (_tiles.TryGetValue(_theaterKey, out var th)) th.Panel.Visible = false;
                if (keys.Count == 0) return;

                const int sgap = 8;
                int stripCellH = Math.Max(40, h - 10);
                int stripCellW = stripCellH * 16 / 9;
                int total = keys.Count * stripCellW + (keys.Count - 1) * sgap;
                if (total > w - 16)
                {
                    stripCellW = Math.Max(60, (w - 16 - (keys.Count - 1) * sgap) / keys.Count);
                    stripCellH = Math.Min(stripCellH, stripCellW * 9 / 16);
                    total = keys.Count * stripCellW + (keys.Count - 1) * sgap;
                }
                int sx = (w - total) / 2;
                int sy = (h - stripCellH) / 2;
                foreach (var k in keys)
                {
                    if (!_tiles.TryGetValue(k, out var t)) continue;
                    t.Panel.Visible = true;
                    t.Panel.SetBounds(sx, sy, stripCellW, stripCellH);
                    t.Panel.Invalidate();
                    sx += stripCellW + sgap;
                }
                return;
            }

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
