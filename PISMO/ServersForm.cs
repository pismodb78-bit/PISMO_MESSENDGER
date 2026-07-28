using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Серверы как в Discord: список серверов, текстовые и голосовые каналы,
    /// чат в текстовом канале, подключение к голосовому (LiveKit-комната
    /// "vch_<channelId>"), участники с базовым управлением (кик/бан для владельца).
    /// Роли с правами и @упоминания — следующая итерация (таблицы уже заведены).
    /// </summary>
    public sealed partial class ServersForm : Form
    {
        private int _serverId = -1;
        private int _pendingOpenServerId = -1;   // сервер, который надо открыть сразу после загрузки (из рейла)
        private string _serverName = "";
        private bool _isOwner = false;
        private int _channelId = -1;
        private string _channelType = "text";
        private string _channelName = "";

        // Права текущего пользователя на выбранном сервере и заглушение.
        private bool _canBan, _canKick, _canMute, _canManage, _serverMuted;

        private readonly int _me = UserSession.EffectiveId;
        private string _myLogin = "";
        private string _myRoleName = "";

        private FlowLayoutPanel _pnlServers;
        private FlowLayoutPanel _pnlChannels;
        private FlowLayoutPanel _pnlMembers;
        private FlowLayoutPanel _pnlMessages;
        private Panel _pnlInput;
        private TextBox _txtInput;
        private Label _lblTitle;

        // Отложенное вложение канала (файл/картинка ждёт нажатия «Отправить»,
        // а не улетает сразу при перетаскивании/выборе) — как в мессенджере.
        private byte[] _chPendingImg;
        private byte[] _chPendingFile;
        private string _chPendingFileName;
        private Panel _chPreview;      // полоска-превью над полем ввода
        private Label _chPreviewLbl;
        private System.Windows.Forms.Timer _refresh;
        private int _lastMsgCount = -1;

        // Контейнеры участников «в эфире» под каждым голосовым каналом.
        private readonly Dictionary<int, FlowLayoutPanel> _voiceContainers = new();
        private readonly Dictionary<int, string> _voiceSig = new(); // подпись «кто в эфире» — чтобы не перестраивать каждые 2.5с
        private readonly ToolTip _voiceMemberTip = new ToolTip();    // полное имя участника (при обрезке «…»)
        private static readonly Font _vmNameFont = new Font("Segoe UI", 8.5f);
        private static readonly Font _vmBadgeFont = new Font("Segoe UI Semibold", 7f, FontStyle.Bold);
        private TextBox _serverSearch;
        private TextBox _channelSearch;

        // Ответ (reply) на сообщение канала.
        private Panel _replyBar;
        private Panel _bottomDock;
        private Label _lblReply;
        private int _replyToId = -1;
        private static bool _replyColOk = true; // есть ли колонка reply_to_id (миграция)
        private readonly Dictionary<int, Control> _msgControls = new(); // id сообщения -> контрол для перехода
        private readonly Dictionary<int, DataTable> _chanMetaCache = new(); // channelId -> meta (мгновенное открытие)
        // Наличие медиа-колонок определяем по факту (information_schema), а не
        // залипающим флагом: иначе одна ранняя ошибка 1054 навсегда блокировала медиа.
        // Кешируем только положительный результат; пока false — перепроверяем
        // (чтобы подхватить миграцию без перезапуска приложения).
        private static bool _mediaColPresent;
        private static bool MediaColumnsExist()
        {
            if (_mediaColPresent) return true;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                    "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='server_messages' " +
                    "AND COLUMN_NAME IN ('image_data','audio_data','video_data','file_data','file_name')", conn);
                _mediaColPresent = Convert.ToInt64(cmd.ExecuteScalar()) >= 5;
            }
            catch { _mediaColPresent = false; }
            return _mediaColPresent;
        }
        // Медиа сообщений канала в памяти (id -> байты). В дисковый кеш не пишется.
        private readonly Dictionary<int, (byte[] img, byte[] audio, byte[] video, byte[] file, string fname)> _serverMedia = new();
        // Запись голосового в канал.
        private WaveInEvent _chWaveIn;
        private MemoryStream _chAudioStream;
        private WaveFileWriter _chWaveWriter;
        private Button _btnChVoice;

        // Автоподсказка @упоминаний при вводе.
        private Form _mentionPopup;
        private ListBox _mentionList;
        private readonly List<(string token, string display, string desc)> _mentionItems = new();
        private int _mentionAtPos = -1;   // позиция '@' в тексте, для которого открыта подсказка

        /// <summary>Открыть окно серверов сразу на конкретном сервере (клик по иконке в рейле).</summary>
        public ServersForm(int openServerId) : this() { _pendingOpenServerId = openServerId; }

        public ServersForm()
        {
            this.Load += (s, e) => { try { Theme.Apply(this); } catch { } };
            // Плашка «Пересылка…» появляется, когда переключаешься в это окно
            // с активной пересылкой из ЛС/группы.
            this.Activated += (s, e) => { try { UpdateForwardNotice(); } catch { } };
            Text = "PISMO — Серверы";
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(54, 57, 63);
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT login FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", _me);
                _myLogin = cmd.ExecuteScalar()?.ToString() ?? "";
            }
            catch { }

            BuildUi();
            EnableChannelFileDrop(_pnlMessages);   // перетаскивание файлов в канал
            EnableChannelFileDrop(_txtInput);
            // Список каналов меняет ширину при ресайзе окна — тянем строки «в эфире»
            // на всю ширину панели, чтобы бейдж «В ЭФИРЕ» был у ПРАВОГО края поля,
            // а имя не сжималось под него.
            if (_pnlChannels != null) _pnlChannels.SizeChanged += (s, e) => StretchVoiceRows();
            Load += (s, e) =>
            {
                LoadServers();
                if (_pendingOpenServerId > 0) { SelectServerById(_pendingOpenServerId); _pendingOpenServerId = -1; }
            };

            // Сообщения — real-time по WS (OnWs); опрос сообщений только как ФОЛБЭК,
            // когда WS не подключён. «Кто в эфире» обновляем всегда, но через дифф
            // (перестроение лишь при изменении состава) — это дёшево.
            _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
            _refresh.Tick += (s, e) =>
            {
                // Встроенное окно скрыто (пользователь в ЛС) — не тратим запросы/такты.
                if (!Visible) return;
                // Сообщения канала — по WS (broadcast); опрос только при обрыве WS.
                if (!WebSocketSignalingClient.Instance.IsConnected
                    && _channelId > 0) MaybeReloadMessages();
                RefreshVoicePresence(); // presence нет в WS — обновляем (диффом, дёшево)
                RefreshMemberPresence(); // статус участников сервера (точки), без пересборки
            };
            _refresh.Start();

            // Подгружаем аватарки участников «в эфире» при готовности.
            AvatarStore.AvatarLoaded += OnAvatarLoadedForVoice;

            WebSocketSignalingClient.Instance.OnMessageReceived += OnWs;
            FormClosed += (s, e) =>
            {
                try { WebSocketSignalingClient.Instance.OnMessageReceived -= OnWs; } catch { }
                try { _refresh.Stop(); _refresh.Dispose(); } catch { }
                try { AvatarStore.AvatarLoaded -= OnAvatarLoadedForVoice; } catch { }
                try { _mentionPopup?.Dispose(); } catch { }
            };
        }

        private void OnAvatarLoadedForVoice(int uid)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    foreach (var cont in _voiceContainers.Values)
                        foreach (Control row in cont.Controls)
                            foreach (Control c in row.Controls)
                                if (c is Panel) { try { c.Invalidate(); } catch { } }
                    try { _footerAvatar?.Invalidate(); } catch { }
                    // Список участников справа: перерисовываем кнопку этого uid.
                    foreach (var (mUid, mBtn) in _memberButtons)
                        if (mUid == uid) { try { if (!mBtn.IsDisposed) mBtn.Invalidate(); } catch { } }
                }));
            }
            catch { }
        }

        // ── Футер профиля под списком каналов (2.1) — как плашка в сайдбаре ЛС:
        //    аватар + имя, кнопки 🎤(мьют)/▾ 🎧(заглушить всё)/▾ и ⚙ настройки.
        //    Состояние общее с футером ЛС (VoiceState через MainForm.Current). ──
        private Panel _footerAvatar;
        private Action _footerRepaint;

        private Panel BuildChannelFooter()
        {
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 88, Width = 200, BackColor = Color.FromArgb(28, 29, 34) };
            footer.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r = new Rectangle(4, 4, footer.Width - 8, footer.Height - 8);
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                const int d = 20;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                // Кастомный Paint не проходит через Theme.Apply — красим через Map,
                // чтобы плашка не оставалась тёмной в светлой теме.
                using var br = new SolidBrush(Theme.Map(Color.FromArgb(35, 36, 41)));
                g.FillPath(br, path);
            };

            // Нижний ряд: аватар + имя.
            var avatar = new Panel { Size = new Size(36, 36), Location = new Point(12, 44), BackColor = Color.Transparent };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, _me, 0, 0, avatar.Width - 1))
                {
                    using var br = new SolidBrush(Color.FromArgb(88, 101, 242));
                    e.Graphics.FillEllipse(br, 0, 0, avatar.Width - 1, avatar.Height - 1);
                    string letter = (UserSession.EffectiveName ?? "?").Trim();
                    letter = letter.Length > 0 ? letter[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 13f, FontStyle.Bold);
                    var sz = e.Graphics.MeasureString(letter, f);
                    e.Graphics.DrawString(letter, f, Brushes.White,
                        (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
                }
            };
            _footerAvatar = avatar;
            try { AvatarStore.EnsureLoaded(_me); } catch { }

            var lblMe = new Label
            {
                Text = UserSession.EffectiveName ?? "",
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Location = new Point(54, 48),
                Size = new Size(footer.Width - 62, 28),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            // Верхний ряд: кнопки справа налево ⚙ ▾ 🎧 ▾ 🎤 (как в футере ЛС).
            Button Mk(string text, int w, int x, float size = 10.5f)
            {
                var b = new Button
                {
                    Text = text,
                    Font = new Font("Segoe UI", size),
                    Size = new Size(w, 30),
                    Location = new Point(x, 8),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(185, 187, 190),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 52, 58);
                return b;
            }

            int W = footer.Width;
            var btnSet = Mk("⚙", 30, W - 40, 12f);
            var btnSpkArrow = Mk("▾", 16, W - 58, 8f);
            var btnSpk = Mk("🎧", 30, W - 90);
            var btnMicArrow = Mk("▾", 16, W - 108, 8f);
            var btnMic = Mk("🎤", 30, W - 140);

            var tip = new ToolTip();
            void PaintStates()
            {
                if (footer.IsDisposed) return;
                var idle = Theme.Map(Color.FromArgb(185, 187, 190));   // серый — под тему
                btnMic.Text = VoiceState.MicMuted ? "🔇" : "🎤";
                btnMic.ForeColor = VoiceState.MicMuted ? Color.FromArgb(237, 66, 69) : idle;
                btnSpk.Text = VoiceState.Deafened ? "🔕" : "🎧";
                btnSpk.ForeColor = VoiceState.Deafened ? Color.FromArgb(237, 66, 69) : idle;
                tip.SetToolTip(btnMic, VoiceState.MicMuted ? "Включить микрофон" : "Выключить микрофон");
                tip.SetToolTip(btnSpk, VoiceState.Deafened ? "Включить звук" : "Отключить звук");
            }
            PaintStates();
            tip.SetToolTip(btnSet, "Настройки устройств");
            tip.SetToolTip(btnMicArrow, "Устройство ввода");
            tip.SetToolTip(btnSpkArrow, "Устройство вывода");

            btnMic.Click += (s, e) => MainForm.Current?.ToggleMicGlobal();
            btnSpk.Click += (s, e) => MainForm.Current?.ToggleDeafenGlobal();
            btnMicArrow.Click += (s, e) =>
            {
                var m = MainForm.Current?.BuildMicDeviceMenu();
                if (m != null) m.Show(btnMicArrow, new Point(0, -m.Items.Count * 24));
            };
            btnSpkArrow.Click += (s, e) =>
            {
                var m = MainForm.Current?.BuildSpeakerDeviceMenu();
                if (m != null) m.Show(btnSpkArrow, new Point(0, -m.Items.Count * 24));
            };
            // То же меню, что и ⚙ в мессенджере (профиль/пароль/устройства/тема/…),
            // а не сразу «Настройки устройств».
            btnSet.Click += (s, e) =>
            {
                try
                {
                    var mf = MainForm.Current;
                    if (mf != null && !mf.IsDisposed) mf.ShowSettingsMenu(btnSet);
                    else new SettingsForm().ShowDialog(this);
                }
                catch { }
            };

            // Синхронизация с футером ЛС: мьют по хоткею/в звонке/в другом окне
            // сразу перекрашивает и эти кнопки.
            _footerRepaint = PaintStates;
            try { if (MainForm.Current != null) MainForm.Current.FooterVoiceChanged += _footerRepaint; } catch { }
            FormClosed += (s, e) =>
            {
                try { if (MainForm.Current != null && _footerRepaint != null) MainForm.Current.FooterVoiceChanged -= _footerRepaint; } catch { }
            };

            footer.Controls.Add(avatar);
            footer.Controls.Add(lblMe);
            footer.Controls.Add(btnMic);
            footer.Controls.Add(btnMicArrow);
            footer.Controls.Add(btnSpk);
            footer.Controls.Add(btnSpkArrow);
            footer.Controls.Add(btnSet);
            return footer;
        }

        // ── Голосовой док в серверном виде ──────────────────────────────
        // В серверном виде основной сайдбар MainForm (с доком «Голосовая связь
        // подключена») скрыт, поэтому переносим ТОТ ЖЕ САМЫЙ док в колонку каналов
        // (1-в-1, без копий). Ссылку на контейнер задаёт Designer.
        internal Panel ChannelHost;

        /// <summary>Вставить голосовой док MainForm в колонку каналов (над футером).</summary>
        public void MountDock(Control dock)
        {
            try
            {
                if (ChannelHost == null || dock == null) return;
                if (dock.Parent != ChannelHost)
                {
                    dock.Parent?.Controls.Remove(dock);
                    ChannelHost.Controls.Add(dock);
                }
                dock.Dock = DockStyle.Bottom;
                // Индекс 1 → док над футером (футер уходит в самый низ).
                ChannelHost.Controls.SetChildIndex(dock, 1);
            }
            catch { }
        }

        private void OnWs(string type, int senderId, int sessionId, string payload)
        {
            if (type == "new_message" && payload == "server" && sessionId == _channelId)
            {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(LoadMessages)); } catch { }
            }
        }


        private Panel _serverHostCol;   // колонка со списком серверов (прячем при встраивании)

        /// <summary>Переводит окно в режим встраивания в MainForm (как Discord —
        /// одно окно): убирает рамку/заголовок и прячет колонку серверов, т.к. её
        /// роль выполняет левый рейл в MainForm.</summary>
        public void EnterEmbeddedMode()
        {
            try
            {
                TopLevel = false;
                FormBorderStyle = FormBorderStyle.None;
                MinimumSize = new Size(0, 0);   // иначе как Dock=Fill-ребёнок не сожмётся < 820x520
                Dock = DockStyle.Fill;
                if (_serverHostCol != null) _serverHostCol.Visible = false;
            }
            catch { }
        }

        /// <summary>Диалог «создать/войти на сервер» для кнопки «+» в рейле.</summary>
        public void AddServerDialog()
        {
            var r = MessageBox.Show(
                "Создать новый сервер?\n\nДа — создать новый, Нет — войти по ID.",
                "PISMO — серверы", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) CreateServer();
            else if (r == DialogResult.No) JoinServer();
        }

        // ── Серверы ─────────────────────────────────────────────────────
        private void LoadServers()
        {
            _pnlServers.Controls.Clear();
            _pnlServers.Controls.Add(MakeHeader("Серверы"));
            var btnCreate = MakeSideButton("➕ Создать сервер", Color.FromArgb(59, 165, 93));
            btnCreate.Click += (s, e) => CreateServer();
            _pnlServers.Controls.Add(btnCreate);
            var btnJoin = MakeSideButton("🔑 Войти по ID", Color.FromArgb(88, 101, 242));
            btnJoin.Click += (s, e) => JoinServer();
            _pnlServers.Controls.Add(btnJoin);

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT s.id, s.name, s.owner_id FROM servers s JOIN server_members m ON m.server_id=s.id WHERE m.user_id=@me ORDER BY s.id", conn);
                cmd.Parameters.AddWithValue("@me", _me);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int sid = Convert.ToInt32(r["id"]);
                    string name = r["name"].ToString();
                    bool owner = Convert.ToInt32(r["owner_id"]) == _me;
                    var b = MakeSideButton((owner ? "👑 " : "🗗 ") + name, Color.FromArgb(64, 68, 75));
                    b.Tag = sid;
                    b.AccessibleName = name;
                    b.Click += (s, e) => SelectServer(sid, name, owner);
                    _pnlServers.Controls.Add(b);
                }
                FilterServers(_serverSearch?.Text);
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void CreateServer()
        {
            string name = Prompt("Название сервера");
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                long sid;
                using (var cmd = new MySqlCommand("INSERT INTO servers (name, owner_id) VALUES (@n,@o)", conn))
                {
                    cmd.Parameters.AddWithValue("@n", name);
                    cmd.Parameters.AddWithValue("@o", _me);
                    cmd.ExecuteNonQuery();
                    sid = cmd.LastInsertedId;
                }
                using (var cmd = new MySqlCommand("INSERT INTO server_members (server_id, user_id) VALUES (@s,@u)", conn))
                { cmd.Parameters.AddWithValue("@s", sid); cmd.Parameters.AddWithValue("@u", _me); cmd.ExecuteNonQuery(); }
                // Каналы по умолчанию.
                using (var cmd = new MySqlCommand("INSERT INTO server_channels (server_id,name,type,position) VALUES (@s,'основной','text',0),(@s,'Общий','voice',1)", conn))
                { cmd.Parameters.AddWithValue("@s", sid); cmd.ExecuteNonQuery(); }

                MessageBox.Show($"Сервер создан. ID для приглашений: {sid}", "PISMO");
                LoadServers();
                SelectServer((int)sid, name, true);
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void JoinServer()
        {
            string idStr = Prompt("ID сервера (его сообщает владелец)");
            if (!int.TryParse(idStr, out int sid)) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var ban = new MySqlCommand("SELECT 1 FROM server_bans WHERE server_id=@s AND user_id=@u", conn))
                { ban.Parameters.AddWithValue("@s", sid); ban.Parameters.AddWithValue("@u", _me); if (ban.ExecuteScalar() != null) { MessageBox.Show("Вы забанены на этом сервере.", "PISMO"); return; } }
                using (var chk = new MySqlCommand("SELECT name, owner_id FROM servers WHERE id=@s", conn))
                {
                    chk.Parameters.AddWithValue("@s", sid);
                    using var r = chk.ExecuteReader();
                    if (!r.Read()) { MessageBox.Show("Сервер не найден.", "PISMO"); return; }
                }
                using (var cmd = new MySqlCommand("INSERT IGNORE INTO server_members (server_id,user_id) VALUES (@s,@u)", conn))
                { cmd.Parameters.AddWithValue("@s", sid); cmd.Parameters.AddWithValue("@u", _me); cmd.ExecuteNonQuery(); }
                LoadServers();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        /// <summary>Открыть сервер по id (для внешних вызовов из рейла ЛС/серверов).</summary>
        public void OpenServer(int sid)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(() => SelectServerById(sid))); return; }
            }
            catch { }
            SelectServerById(sid);
        }

        /// <summary>Подтянуть имя/владельца сервера и выбрать его.</summary>
        private void SelectServerById(int sid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT name, owner_id FROM servers WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@s", sid);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    string name = r["name"].ToString();
                    bool owner = Convert.ToInt32(r["owner_id"]) == _me;
                    r.Close();
                    SelectServer(sid, name, owner);
                }
            }
            catch { }
        }

        private void SelectServer(int sid, string name, bool owner)
        {
            _serverId = sid; _serverName = name; _isOwner = owner; _channelId = -1; _lastMsgCount = -1;
            _lblTitle.Text = name;
            MainForm.DisposeAndClear(_pnlMessages);
            _renderedKey = null; _renderedSig = null;
            _pnlInput.Visible = false;
            ComputePerms();
            LoadChannels();
            LoadMembers();
            PrefetchServerChannels();
        }

        /// <summary>Фоновая прогрузка всех текстовых каналов сервера в кеш (текст +
        /// медиа на диск), чтобы открытие любого канала и его видео было мгновенным.</summary>
        private void PrefetchServerChannels()
        {
            int sid = _serverId;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var chans = new List<int>();
                    using (var conn = DBHelper.OpenConnection())
                    using (var cmd = new MySqlCommand("SELECT id FROM server_channels WHERE server_id=@s AND type='text'", conn))
                    {
                        cmd.Parameters.AddWithValue("@s", sid);
                        using var rd = cmd.ExecuteReader();
                        while (rd.Read()) chans.Add(rd.GetInt32(0));
                    }
                    foreach (var ch in chans)
                    {
                        if (_serverId != sid || IsDisposed) break; // сменили сервер
                        var media = new Dictionary<int, (byte[] img, byte[] audio, byte[] video, byte[] file, string fname)>();
                        var dt = FetchChannelMessages(ch, media); // кеширует медиа на диск
                        if (dt == null) continue;
                        try { MessageCache.Save(MessageCache.ChannelKey(ch), dt); } catch { }
                        if (IsDisposed || !IsHandleCreated) continue;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                _chanMetaCache[ch] = dt;
                                foreach (var kv in media) _serverMedia[kv.Key] = kv.Value;
                            }));
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        /// <summary>Считает права текущего пользователя на сервере и его mute.</summary>
        private void ComputePerms()
        {
            _canBan = _canKick = _canMute = _canManage = _isOwner;
            _serverMuted = false;
            _myRoleName = "";
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.muted_notifs, r.name AS rname, r.can_ban, r.can_kick, r.can_mute, r.can_manage " +
                    "FROM server_members m LEFT JOIN server_roles r ON r.id=m.role_id " +
                    "WHERE m.server_id=@s AND m.user_id=@u", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                cmd.Parameters.AddWithValue("@u", _me);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    _serverMuted = r["muted_notifs"] != DBNull.Value && Convert.ToInt32(r["muted_notifs"]) == 1;
                    _myRoleName = r["rname"] == DBNull.Value ? "" : r["rname"].ToString();
                    if (!_isOwner)
                    {
                        bool B(string c) => r[c] != DBNull.Value && Convert.ToInt32(r[c]) == 1;
                        _canBan |= B("can_ban"); _canKick |= B("can_kick");
                        _canMute |= B("can_mute"); _canManage |= B("can_manage");
                    }
                }
            }
            catch { }
        }

        // ── Каналы ──────────────────────────────────────────────────────
        private void LoadChannels()
        {
            _pnlChannels.Controls.Clear();
            _pnlChannels.Controls.Add(MakeHeader("Каналы"));

            // Заглушение сервера (для всех участников).
            var mute = MakeSideButton(_serverMuted ? "🔕 Сервер заглушён" : "🔔 Уведомления вкл", Color.FromArgb(47, 49, 54));
            mute.Click += (s, e) => ToggleServerMute();
            _pnlChannels.Controls.Add(mute);

            if (_canManage)
            {
                var add = MakeSideButton("➕ Канал", Color.FromArgb(59, 165, 93));
                add.Click += (s, e) => CreateChannel();
                _pnlChannels.Controls.Add(add);
                var roles = MakeSideButton("⚙ Роли", Color.FromArgb(88, 101, 242));
                roles.Click += (s, e) => ManageRoles();
                _pnlChannels.Controls.Add(roles);
                var inv = MakeSideButton("ℹ ID сервера: " + _serverId, Color.FromArgb(47, 49, 54));
                _pnlChannels.Controls.Add(inv);
            }
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT id,name,type FROM server_channels WHERE server_id=@s ORDER BY position,id", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                _voiceContainers.Clear();
                _voiceSig.Clear();

                // Группировка как в Discord: сначала «Текстовые каналы», затем «Голосовые».
                bool textHeader = false, voiceHeader = false;
                foreach (DataRow r in dt.Rows)
                {
                    if (r["type"].ToString() != "text") continue;
                    if (!textHeader) { _pnlChannels.Controls.Add(MakeHeader("Текстовые каналы")); textHeader = true; }
                    AddChannelButton(Convert.ToInt32(r["id"]), r["name"].ToString(), "text");
                }
                foreach (DataRow r in dt.Rows)
                {
                    if (r["type"].ToString() != "voice") continue;
                    if (!voiceHeader) { _pnlChannels.Controls.Add(MakeHeader("Голосовые каналы")); voiceHeader = true; }
                    AddChannelButton(Convert.ToInt32(r["id"]), r["name"].ToString(), "voice");
                }
                FilterChannels(_channelSearch?.Text);
            }
            catch (Exception ex) { ShowDbError(ex); }

            RefreshVoicePresence();
        }

        /// <summary>Рисует кнопку канала (+ контейнер «в эфире» для голосовых).</summary>
        private void AddChannelButton(int cid, string cname, string ctype)
        {
            var b = MakeSideButton((ctype == "voice" ? "🔊 " : "# ") + cname, Color.FromArgb(54, 57, 63));
            b.AccessibleName = cname;
            // ЛКМ: текстовый — чат; голосовой — сразу подключение к звонку (+чат).
            b.Click += (s, e) => SelectChannel(cid, ctype, cname);
            // ПКМ: меню действий канала.
            b.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowChannelMenu(cid, ctype, cname); };
            _pnlChannels.Controls.Add(b);

            if (ctype == "voice")
            {
                // Значок 💬 справа (как в Discord): открыть чат канала БЕЗ входа в звонок.
                var bubble = new Label
                {
                    Text = "💬",
                    AutoSize = false,
                    Size = new Size(26, 26),
                    TextAlign = ContentAlignment.MiddleCenter,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(185, 187, 190),
                    Cursor = Cursors.Hand
                };
                bubble.Click += (s, e) => SelectChannel(cid, ctype, cname, joinVoice: false);
                b.Controls.Add(bubble);
                void PlaceBubble(object s, EventArgs e)
                { try { bubble.Location = new Point(b.Width - bubble.Width - 4, (b.Height - bubble.Height) / 2); } catch { } }
                b.Resize += PlaceBubble;
                PlaceBubble(null, null);
            }

            if (ctype == "voice")
            {
                var cont = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Width = 180,
                    Margin = new Padding(10, 0, 0, 4),
                    BackColor = Color.Transparent
                };
                cont.AccessibleName = cname;
                _voiceContainers[cid] = cont;
                _pnlChannels.Controls.Add(cont);
            }
        }

        /// <summary>Разовое обновление сервера (вместо периодического опроса).</summary>
        private void RefreshNow()
        {
            LoadServers();
            if (_serverId <= 0) return;
            LoadChannels();
            LoadMembers();
            if (_channelId > 0) LoadMessages();
            RefreshVoicePresence();
        }

        private void FilterServers(string q)
        {
            q = (q ?? "").Trim().ToLowerInvariant();
            foreach (Control c in _pnlServers.Controls)
                if (c.Tag is int) // только карточки серверов (Tag=sid), заголовок/кнопки не трогаем
                    c.Visible = q.Length == 0 || (c.AccessibleName ?? "").ToLowerInvariant().Contains(q);
        }

        private void FilterChannels(string q)
        {
            q = (q ?? "").Trim().ToLowerInvariant();
            foreach (Control c in _pnlChannels.Controls)
                if (!string.IsNullOrEmpty(c.AccessibleName)) // карточки каналов/контейнеры «в эфире»
                    c.Visible = q.Length == 0 || c.AccessibleName.ToLowerInvariant().Contains(q);
        }

        /// <summary>Обновляет списки участников «в эфире» под голосовыми каналами.
        /// Чтение БД — в фоне, отрисовка — на UI-потоке.</summary>
        private void RefreshVoicePresence()
        {
            if (_serverId <= 0 || _voiceContainers.Count == 0) return;
            int serverId = _serverId;
            System.Threading.Tasks.Task.Run(() =>
            {
                var map = VoicePresence.ReadForServer(serverId);
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (_serverId != serverId) return;
                        foreach (var kv in _voiceContainers)
                        {
                            var cont = kv.Value;
                            if (cont.IsDisposed) continue;
                            map.TryGetValue(kv.Key, out var people);
                            // Перестраиваем строки ТОЛЬКО если состав изменился —
                            // иначе каждые 2.5с дёргался UI (периодический лаг).
                            string sig = VoiceSig(people);
                            if (_voiceSig.TryGetValue(kv.Key, out var old) && old == sig) continue;
                            _voiceSig[kv.Key] = sig;
                            UpdateVoiceContainer(cont, people);
                        }
                    }));
                }
                catch { }
            });
        }

        private static string VoiceSig(List<(int uid, string name, bool streaming, bool micMuted, bool deafened)> people)
        {
            if (people == null || people.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var p in people)
                sb.Append(p.uid).Append(p.streaming ? 's' : '_')
                  .Append(p.micMuted ? 'm' : '_').Append(p.deafened ? 'd' : '_').Append('|');
            return sb.ToString();
        }

        private void UpdateVoiceContainer(FlowLayoutPanel cont, List<(int uid, string name, bool streaming, bool micMuted, bool deafened)> people)
        {
            cont.SuspendLayout();
            cont.Controls.Clear();
            if (people != null)
            {
                foreach (var (uid, name, streaming, micMuted, deafened) in people)
                    cont.Controls.Add(MakeVoiceMemberRow(uid, name, streaming, micMuted, deafened));
            }
            cont.ResumeLayout();
        }

        /// <summary>Строка участника голосового канала: аватар + имя, а бейдж
        /// «В ЭФИРЕ» — только если включена камера или демонстрация экрана.</summary>
        // Доступная ширина строки участника = вся ширина панели каналов за вычетом
        // её паддинга и левого отступа контейнера. Так строка (и бейдж на её правом
        // краю) занимает всё поле, а не фиксированные 178px посреди сайдбара.
        private int VoiceRowWidth()
        {
            int w = 178;
            try
            {
                if (_pnlChannels != null && _pnlChannels.ClientSize.Width > 60)
                    w = _pnlChannels.ClientSize.Width - _pnlChannels.Padding.Horizontal - 12;
            }
            catch { }
            return Math.Max(120, w);
        }

        // Перетянуть уже созданные строки «в эфире» под текущую ширину панели.
        private void StretchVoiceRows()
        {
            int w = VoiceRowWidth();
            foreach (var cont in _voiceContainers.Values)
            {
                if (cont == null || cont.IsDisposed) continue;
                foreach (Control row in cont.Controls)
                    if (row.Width != w) { row.Width = w; row.Invalidate(); }
            }
        }

        private Control MakeVoiceMemberRow(int uid, string name, bool streaming,
                                           bool micMuted = false, bool deafened = false)
        {
            string full = name ?? "";
            var row = new Panel { Width = VoiceRowWidth(), Height = 30, Margin = new Padding(0, 0, 0, 2), BackColor = Color.Transparent };
            _voiceMemberTip.SetToolTip(row, full);   // полное имя — тултипом у курсора

            // Аватар — отдельным контролом (асинхронная подгрузка картинки).
            var av = new Panel { Size = new Size(22, 22), Location = new Point(0, 4), BackColor = Color.Transparent };
            av.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, uid, 0, 0, av.Width - 1))
                {
                    int h = 0; foreach (char ch in full) h = (h * 31 + ch) & 0x7fffffff;
                    Color[] pal = { Color.FromArgb(88,101,242), Color.FromArgb(235,69,158),
                        Color.FromArgb(59,165,93), Color.FromArgb(250,166,26), Color.FromArgb(0,176,244) };
                    using var br = new SolidBrush(pal[h % pal.Length]);
                    e.Graphics.FillEllipse(br, 0, 0, av.Width - 1, av.Height - 1);
                    string letter = full.Length > 0 ? full.Substring(0, 1).ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 8f, FontStyle.Bold);
                    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString(letter, f, Brushes.White, new RectangleF(0, 0, av.Width, av.Height), sf);
                }
            };
            row.Controls.Add(av);

            // Имя + бейдж + значок мьюта РИСУЕМ САМИ в Paint строки — по её реальной
            // ширине. Никаких Label/Anchor/AutoSize: имя гарантированно обрезается «…»
            // ВНУТРИ прямоугольника, который заканчивается ДО бейджа → наложения нет.
            row.Paint += (s, e) =>
            {
                var g = e.Graphics;
                int rightEdge = row.ClientSize.Width - 4;

                if (streaming)
                {
                    int bw = TextRenderer.MeasureText(g, "В ЭФИРЕ", _vmBadgeFont).Width + 10;
                    var br = new Rectangle(rightEdge - bw, 6, bw, 18);
                    using (var b = new SolidBrush(Color.FromArgb(237, 66, 69))) g.FillRectangle(b, br);
                    TextRenderer.DrawText(g, "В ЭФИРЕ", _vmBadgeFont, br, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    rightEdge = br.Left - 6;
                }
                if (deafened || micMuted)
                {
                    const int mSz = 22;
                    var mr = new Rectangle(rightEdge - mSz, (row.Height - mSz) / 2, mSz, mSz);
                    DrawMemberMuteIcon(g, mr, deafened);
                    rightEdge = mr.Left - 6;
                }
                var nameRect = Rectangle.FromLTRB(28, 0, Math.Max(58, rightEdge), row.Height);
                TextRenderer.DrawText(g, full, _vmNameFont, nameRect, Color.FromArgb(210, 211, 213),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };

            AvatarStore.EnsureLoaded(uid);
            return row;
        }

        // Значок мьюта участника в списке канала: наушники (deaf) или микрофон,
        // перечёркнутые красным. Векторно, без эмодзи-шрифтов.
        private static void DrawMemberMuteIcon(Graphics g, Rectangle r, bool deaf)
        {
            // Фон строки канала — тёмно-серый (47,49,54); используем его для «выреза».
            // Рисуем ПО ПОЛОЖЕНИЮ r (раньше было (1,1) — годилось только когда значок
            // был отдельным контролом; из Paint строки нужно учитывать r.X/r.Y).
            MuteGlyph.Draw(g, new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), deaf,
                           Color.FromArgb(47, 49, 54));
        }

        private void CreateChannel()
        {
            string name = Prompt("Название канала");
            if (string.IsNullOrWhiteSpace(name)) return;
            var voice = MessageBox.Show("Голосовой канал?\nДа — голосовой, Нет — текстовый.", "Тип канала",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("INSERT INTO server_channels (server_id,name,type,position) VALUES (@s,@n,@t,99)", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@t", voice ? "voice" : "text");
                cmd.ExecuteNonQuery();
                LoadChannels();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void SelectChannel(int cid, string type, string name, bool joinVoice = true)
        {
            _channelId = cid; _channelType = type; _channelName = name; _lastMsgCount = -1;
            _lblTitle.Text = (type == "voice" ? "🔊 " : "# ") + name;
            MainForm.DisposeAndClear(_pnlMessages);
            _renderedKey = null; _renderedSig = null; // панель очищена — не пропускать отрисовку
            CancelServerReply();
            ClearChannelPending();   // не тащим превью вложения между каналами
            try { UpdateForwardNotice(); } catch { }

            // И у текстового, И у голосового канала — полноценный чат (как в Discord):
            // в голосовом можно писать, находясь в звонке или без него.
            if (_bottomDock != null) _bottomDock.Visible = true;
            _pnlInput.Visible = true;
            LoadMessages();

            // ЛКМ по голосовому каналу = сразу подключение к звонку (кнопки
            // «Подключиться» больше нет). Открыть чат БЕЗ звонка — ПКМ → «Открыть чат».
            if (type == "voice" && joinVoice)
                JoinVoiceChannel(cid, name);

            // Открыл чат — канал прочитан.
            int capC = cid;
            System.Threading.Tasks.Task.Run(() => ServerReads.MarkChannelRead(_me, capC));
        }

        /// <summary>ПКМ по каналу: открыть чат / присоединиться / заглушить / прочитано.</summary>
        private void ShowChannelMenu(int cid, string ctype, string cname)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(24, 25, 28),
                ForeColor = Color.FromArgb(220, 221, 222)
            };
            menu.Items.Add("💬  Открыть чат", null, (s, e) => SelectChannel(cid, ctype, cname, joinVoice: false));
            if (ctype == "voice")
                menu.Items.Add("🔊  Присоединиться к звонку", null, (s, e) => SelectChannel(cid, ctype, cname));
            menu.Items.Add("🔕  Заглушить уведомления сервера", null, (s, e) => ToggleServerMute());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("✓  Пометить прочитанным", null, (s, e) =>
                System.Threading.Tasks.Task.Run(() => ServerReads.MarkChannelRead(_me, cid)));
            menu.Items.Add("✓✓  Пометить ВЕСЬ сервер прочитанным", null, (s, e) =>
                System.Threading.Tasks.Task.Run(() => ServerReads.MarkServerRead(_me, _serverId)));
            menu.Show(Cursor.Position);
        }

        // Уже подключённые голосовые каналы (не заходим в один звонок дважды).
        private readonly HashSet<int> _joinedVoice = new HashSet<int>();

        private void JoinVoiceChannel(int cid, string name)
        {
            if (_joinedVoice.Contains(cid)) return;
            // Нельзя быть в двух голосовых каналах разом: выходим из текущего
            // голоса (личного/серверного) перед входом в новый — иначе две комнаты
            // LiveKit, звук стакается/усиливается и слышен свой голос.
            try
            {
                if (MainForm.Current != null && MainForm.Current.HasActiveVoice())
                {
                    MainForm.Current.EndCurrentVoice();
                    _joinedVoice.Clear();
                }
            }
            catch { }
            _joinedVoice.Add(cid);
            var call = new CallForm("vch_" + cid, name);
            // Показ «Голосовая связь подключена» — тот же док MainForm (в серверном
            // виде он уже перенесён в колонку каналов). Видимостью рулит Notify*.
            call.FormClosed += (a, b) =>
            {
                _joinedVoice.Remove(cid);
                MainForm.Current?.NotifyVoiceEnded();
            };
            call.Show();
            MainForm.Current?.NotifyVoiceStarted($"{name} / {_serverName}", call);
        }

        // ── Ответы (reply) ──────────────────────────────────────────────
        private void BeginServerReply(int id, string text)
        {
            _replyToId = id;
            string preview = text.StartsWith("gif:", StringComparison.OrdinalIgnoreCase)
                ? "[GIF]" : (text.Length > 60 ? text.Substring(0, 60) + "…" : text);
            _lblReply.Text = "↩ Ответ: " + preview;
            _replyBar.Height = 26;
            _replyBar.Visible = true;
            UpdateBottomHeight();
            _txtInput.Focus();
        }

        private void CancelServerReply()
        {
            _replyToId = -1;
            if (_replyBar != null) { _replyBar.Visible = false; _replyBar.Height = 0; }
            UpdateBottomHeight();
        }

        /// <summary>Пересчитывает высоту нижнего дока: база (поле ввода) + полоска
        /// ответа (если открыта) + превью вложения (если есть).</summary>
        private void UpdateBottomHeight()
        {
            if (_bottomDock == null) return;
            int h = 68;
            if (_replyBar != null && _replyBar.Visible) h += _replyBar.Height;
            if (_chPreview != null && _chPreview.Visible) h += _chPreview.Height;
            _bottomDock.Height = h;
        }

        // ── Сообщения канала ────────────────────────────────────────────
        private void MaybeReloadMessages()
        {
            // COUNT(*) делаем в фоне, чтобы не подвешивать UI каждые 2.5 c
            // (особенно заметно на высоком пинге к БД).
            int ch = _channelId;
            if (ch <= 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                int n;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand("SELECT COUNT(*) FROM server_messages WHERE channel_id=@c", conn);
                    cmd.Parameters.AddWithValue("@c", ch);
                    n = Convert.ToInt32(cmd.ExecuteScalar());
                }
                catch { return; }
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (_channelId == ch && n != _lastMsgCount) LoadMessages();
                    }));
                }
                catch { }
            });
        }

        private void LoadMessages()
        {
            if (_channelId <= 0) return;
            int channel = _channelId;

            // 1) Мгновенно рисуем из кеша (память → диск), чтобы канал открывался без задержек.
            if (!_chanMetaCache.TryGetValue(channel, out var cachedDt))
            {
                cachedDt = MessageCache.Load(MessageCache.ChannelKey(channel));
                if (cachedDt != null) _chanMetaCache[channel] = cachedDt;
            }
            if (cachedDt != null) RenderMessages(cachedDt, channel);

            // 2) Свежие данные тянем в ФОНЕ и перерисовываем, если всё ещё в канале.
            System.Threading.Tasks.Task.Run(() =>
            {
                var media = new Dictionary<int, (byte[] img, byte[] audio, byte[] video, byte[] file, string fname)>();
                DataTable dt = FetchChannelMessages(channel, media);
                if (dt == null) return;
                // В кеш на диск пишем только метаданные (без тяжёлых BLOB).
                try { MessageCache.Save(MessageCache.ChannelKey(channel), dt); } catch { }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_channelId != channel) return; // уже переключились
                        foreach (var kv in media) _serverMedia[kv.Key] = kv.Value;
                        _chanMetaCache[channel] = dt;
                        RenderMessages(dt, channel);
                    }));
                }
                catch { }
            });
        }

        /// <summary>Запрос сообщений канала из БД (в фоне). Тянет медиа в mediaOut,
        /// а из возвращаемой таблицы BLOB-колонки убирает (чтобы кеш оставался лёгким).
        /// Сам обрабатывает отсутствие колонок reply_to_id / медиа (старая БД).</summary>
        private DataTable FetchChannelMessages(int channel,
            Dictionary<int, (byte[] img, byte[] audio, byte[] video, byte[] file, string fname)> mediaOut)
        {
            try
            {
                string replyCols = _replyColOk
                    ? ", sm.reply_to_id, " +
                      "(SELECT TRIM(CONCAT(ru.Name,' ',ru.Surname)) FROM server_messages rsm JOIN users ru ON ru.id=rsm.sender_id WHERE rsm.id=sm.reply_to_id) AS r_sender, " +
                      "(SELECT ru.login FROM server_messages rsm JOIN users ru ON ru.id=rsm.sender_id WHERE rsm.id=sm.reply_to_id) AS r_login, " +
                      "(SELECT rsm.text FROM server_messages rsm WHERE rsm.id=sm.reply_to_id) AS r_text"
                    : "";
                bool media = MediaColumnsExist();
                // Тянем только лёгкие метаданные (наличие медиа), а сами байты —
                // из локального кеша; из БД догружаем ТОЛЬКО то, чего нет в кеше.
                string mediaCols = media
                    ? ", sm.file_name, (sm.image_data IS NOT NULL) AS has_img, (sm.audio_data IS NOT NULL) AS has_audio, (sm.video_data IS NOT NULL) AS has_video, (sm.file_data IS NOT NULL) AS has_file"
                    : "";

                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT sm.id, sm.sender_id, sm.text, sm.created_at, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login" +
                    replyCols + mediaCols +
                    " FROM server_messages sm JOIN users u ON u.id=sm.sender_id WHERE sm.channel_id=@c ORDER BY sm.id ASC", conn);
                cmd.Parameters.AddWithValue("@c", channel);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);

                if (media && mediaOut != null)
                    LoadChannelMedia(conn, dt, mediaOut);
                return dt;
            }
            catch (MySqlException mex) when (mex.Number == 1054)
            {
                // Нет колонки reply_to_id (медиа определяем отдельно через information_schema).
                if (_replyColOk) { _replyColOk = false; return FetchChannelMessages(channel, mediaOut); }
                throw;
            }
            catch (Exception ex)
            {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => ShowDbError(ex))); } catch { }
                return null;
            }
        }

        /// <summary>Заполняет mediaOut байтами медиа канала: сначала из локального
        /// кеша, недостающее догружает из БД одним запросом и кеширует на диск.</summary>
        private void LoadChannelMedia(MySqlConnection conn, DataTable dt,
            Dictionary<int, (byte[] img, byte[] audio, byte[] video, byte[] file, string fname)> mediaOut)
        {
            bool Flag(DataRow r, string c) => dt.Columns.Contains(c) && r[c] != DBNull.Value && Convert.ToBoolean(r[c]);
            var need = new List<(int id, bool ni, bool na, bool nv, bool nf, string fn)>();

            foreach (DataRow r in dt.Rows)
            {
                int id = Convert.ToInt32(r["id"]);
                bool hi = Flag(r, "has_img"), ha = Flag(r, "has_audio"), hv = Flag(r, "has_video"), hf = Flag(r, "has_file");
                if (!hi && !ha && !hv && !hf) continue;
                string fn = dt.Columns.Contains("file_name") && r["file_name"] != DBNull.Value ? r["file_name"].ToString() : null;

                byte[] img = hi ? MediaCache.Get(id, "simg", null) : null;
                byte[] aud = ha ? MediaCache.Get(id, "saudio", null) : null;
                byte[] vid = hv ? MediaCache.Get(id, "svideo", null) : null;
                byte[] fil = hf ? MediaCache.Get(id, "sfile", fn) : null;

                if (img != null || aud != null || vid != null || fil != null)
                    mediaOut[id] = (img, aud, vid, fil, fn);

                bool ni = hi && img == null, na = ha && aud == null, nv = hv && vid == null, nf = hf && fil == null;
                if (ni || na || nv || nf) need.Add((id, ni, na, nv, nf, fn));
            }
            if (need.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            foreach (var n in need) { if (sb.Length > 0) sb.Append(','); sb.Append(n.id); }
            using var c2 = new MySqlCommand(
                $"SELECT id, image_data, audio_data, video_data, file_data, file_name FROM server_messages WHERE id IN ({sb})", conn);
            var fetched = new Dictionary<int, (byte[] i, byte[] a, byte[] v, byte[] f, string fn)>();
            using (var rd = c2.ExecuteReader())
            {
                byte[] B(System.Data.IDataReader rr, int i) => rr.IsDBNull(i) ? null : (byte[])rr.GetValue(i);
                while (rd.Read())
                {
                    int id = rd.GetInt32(0);
                    string fn = rd.IsDBNull(5) ? null : rd.GetString(5);
                    fetched[id] = (B(rd, 1), B(rd, 2), B(rd, 3), B(rd, 4), fn);
                }
            }
            foreach (var n in need)
            {
                if (!fetched.TryGetValue(n.id, out var fb)) continue;
                mediaOut.TryGetValue(n.id, out var cur);
                byte[] img = n.ni ? fb.i : cur.img;
                byte[] aud = n.na ? fb.a : cur.audio;
                byte[] vid = n.nv ? fb.v : cur.video;
                byte[] fil = n.nf ? fb.f : cur.file;
                string fn = n.fn ?? fb.fn;
                mediaOut[n.id] = (img, aud, vid, fil, fn);
                if (n.ni && fb.i != null) MediaCache.Put(n.id, "simg", fb.i);
                if (n.na && fb.a != null) MediaCache.Put(n.id, "saudio", fb.a);
                if (n.nv && fb.v != null) MediaCache.Put(n.id, "svideo", fb.v);
                if (n.nf && fb.f != null) MediaCache.Put(n.id, "sfile", fb.f, fn);
            }
        }

        /// <summary>Отрисовка сообщений канала из готового DataTable.</summary>
        private string _renderedKey;
        private string _renderedSig;

        private void RenderMessages(DataTable dt, int channel)
        {
            if (_channelId != channel) return;

            // Пропускаем повторную отрисовку, если тот же канал и данные/медиа не изменились.
            int mediaForChan = 0;
            foreach (DataRow rr in dt.Rows)
                if (_serverMedia.ContainsKey(Convert.ToInt32(rr["id"]))) mediaForChan++;
            string key = "c" + channel, sig = MainForm.SigOf(dt) + "|m" + mediaForChan;
            if (_renderedKey == key && _renderedSig == sig) return;
            _renderedKey = key; _renderedSig = sig;

            try
            {
                _pnlMessages.SuspendLayout();
                MainForm.DisposeAndClear(_pnlMessages);
                _msgControls.Clear();
                // -80: у бабла слева отступ под аватар (58) + правый паддинг (12).
                // Без этого запаса длинное сообщение делало бабл шире панели →
                // появлялся горизонтальный скроллбар.
                // Ширина «пузыря» = ширина панели − место под вертикальный скролл −
                // отступы бабла (аватар слева 58 + правый паддинг 12) − запас, иначе
                // при появлении вертикального скролла бабл вылезает за край и
                // появляется ГОРИЗОНТАЛЬНЫЙ ползунок. Плюс потолок, как в Discord,
                // чтобы длинные сообщения не растягивались на весь широкий монитор.
                int avail = _pnlMessages.ClientSize.Width
                            - SystemInformation.VerticalScrollBarWidth - 90;
                int msgWidth = Math.Max(120, Math.Min(avail, 900));
                string lastDate = null;
                foreach (DataRow r in dt.Rows)
                {
                    int id = Convert.ToInt32(r["id"]);
                    int senderId = Convert.ToInt32(r["sender_id"]);
                    string nm = r["nm"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"].ToString();
                    string text = Crypto.Dec(r["text"] == DBNull.Value ? "" : r["text"].ToString());
                    DateTime dtMsg = Convert.ToDateTime(r["created_at"]);
                    string time = dtMsg.ToString("HH:mm");

                    // Разделитель по датам (как в ЛС): «13 июля 2026» между днями.
                    string dsep = dtMsg.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("ru-RU"));
                    if (dsep != lastDate)
                    {
                        lastDate = dsep;
                        // Линия-разделитель тянется на всю ширину чата, а не до
                        // потолка бабла (900): иначе на широком окне линия
                        // обрывалась, не доходя до правого края.
                        int sepWidth = Math.Max(120,
                            _pnlMessages.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 20);
                        _pnlMessages.Controls.Add(MainForm.BuildDateSeparatorW(dsep, sepWidth));
                    }

                    // Меня упомянули (@логин/@роль/@все) — подсветим бабл зеленоватым.
                    bool mine = MentionsMe(text);
                    bool isMe = senderId == _me;

                    // Формат — как в Discord: аватар слева, цветной ник, серый бабл
                    // (одинаковый для всех, как входящие в ЛС); упоминание — зеленоватый.
                    const int PAD = 12;
                    const int AVA = 36;                 // диаметр аватарки
                    const int LEFT = PAD + AVA + 10;    // отступ контента правее аватара
                    Color bubbleBg = mine ? Color.FromArgb(47, 68, 55) : Color.FromArgb(48, 51, 58);

                    var holder = new Panel
                    {
                        AutoSize = true,
                        Margin = new Padding(0, 2, 0, 6),
                        Padding = new Padding(LEFT, PAD, PAD, PAD),
                        BackColor = Color.FromArgb(54, 57, 63)   // цвет фона чата — углы «прозрачны»
                    };
                    holder.Paint += (s, e) =>
                    {
                        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var br = new SolidBrush(bubbleBg);
                        e.Graphics.FillRoundedRectangle(br, 0, 0, holder.Width - 1, holder.Height - 1, 10);
                        // Аватар отправителя (кружок) в левом верхнем углу бабла.
                        if (!AvatarStore.DrawAvatar(e.Graphics, senderId, PAD, PAD, AVA - 1))
                        {
                            int h = 0; foreach (char ch in nm) h = (h * 31 + ch) & 0x7fffffff;
                            Color[] pal = { Color.FromArgb(88,101,242), Color.FromArgb(235,69,158),
                                Color.FromArgb(59,165,93), Color.FromArgb(250,166,26), Color.FromArgb(0,176,244) };
                            using var ab = new SolidBrush(pal[h % pal.Length]);
                            e.Graphics.FillEllipse(ab, PAD, PAD, AVA - 1, AVA - 1);
                            string letter = nm.Length > 0 ? nm.Substring(0, 1).ToUpper() : "?";
                            using var f = new Font("Segoe UI Black", 12f, FontStyle.Bold);
                            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            e.Graphics.DrawString(letter, f, Brushes.White, new RectangleF(PAD, PAD, AVA, AVA), sf);
                        }
                    };
                    AvatarStore.EnsureLoaded(senderId);
                    int y = PAD;

                    // Цитата отвечаемого сообщения (если это ответ).
                    int replyToId = _replyColOk && dt.Columns.Contains("reply_to_id") && r["reply_to_id"] != DBNull.Value
                        ? Convert.ToInt32(r["reply_to_id"]) : 0;
                    if (replyToId > 0)
                    {
                        string rs = dt.Columns.Contains("r_sender") && r["r_sender"] != DBNull.Value ? r["r_sender"].ToString().Trim() : "";
                        if (string.IsNullOrWhiteSpace(rs) && dt.Columns.Contains("r_login") && r["r_login"] != DBNull.Value) rs = r["r_login"].ToString();
                        string rt = dt.Columns.Contains("r_text") && r["r_text"] != DBNull.Value ? Crypto.Dec(r["r_text"].ToString()) : "";
                        if (rt.StartsWith("gif:", StringComparison.OrdinalIgnoreCase)) rt = "[GIF]";
                        var quote = new Label
                        {
                            AutoSize = false,
                            Size = new Size(msgWidth - 14, 16),
                            Location = new Point(LEFT, y),
                            ForeColor = Color.FromArgb(0, 176, 244),
                            Font = new Font("Segoe UI", 8f),
                            Cursor = Cursors.Hand,
                            Text = $"↩ {rs}: {(rt.Length > 60 ? rt.Substring(0, 60) + "…" : rt)}"
                        };
                        int targetId = replyToId;
                        quote.Click += (s, e) => ScrollToServerMessage(targetId);
                        holder.Controls.Add(quote);
                        y += 18;
                    }

                    // Ник цветной (стабильный цвет по имени, как в Discord), фон прозрачный —
                    // без тёмного прямоугольника вокруг имени.
                    Color nmColor;
                    {
                        int nh = 0; foreach (char ch in nm) nh = (nh * 31 + ch) & 0x7fffffff;
                        Color[] npal = { Color.FromArgb(129,140,248), Color.FromArgb(240,113,170),
                            Color.FromArgb(87,197,126), Color.FromArgb(250,180,80), Color.FromArgb(84,196,244),
                            Color.FromArgb(196,151,255) };
                        nmColor = npal[nh % npal.Length];
                    }
                    var head = new Label
                    {
                        AutoSize = true,
                        BackColor = Color.Transparent,
                        ForeColor = nmColor,
                        Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                        Location = new Point(LEFT, y),
                        Text = $"{nm} · {time}"
                    };
                    holder.Controls.Add(head);
                    y += 22;

                    // Медиа канала (картинка / голосовое / кружок / видео / файл) — как в обычном чате.
                    if (_serverMedia.TryGetValue(id, out var sm))
                    {
                        if (sm.img is { Length: > 0 })
                        {
                            try
                            {
                                var ms = new MemoryStream(sm.img.ToArray());
                                var img = Image.FromStream(ms);
                                int mw = 420, mh = 360;
                                double rr = Math.Min(2.0, Math.Min((double)mw / img.Width, (double)mh / img.Height));
                                int dw = Math.Max(1, (int)(img.Width * rr)), dh = Math.Max(1, (int)(img.Height * rr));
                                var pb = new PictureBox { SizeMode = PictureBoxSizeMode.StretchImage, Size = new Size(dw, dh), Location = new Point(LEFT, y), Cursor = Cursors.Hand, Image = img };
                                var cap = sm.img;
                                pb.Click += (s, e) => MainForm.ShowImageFullscreen(cap);
                                pb.Disposed += (s, e) => { try { img.Dispose(); ms.Dispose(); } catch { } };
                                holder.Controls.Add(pb); y += dh + 6;
                            }
                            catch { }
                        }
                        if (sm.audio is { Length: > 0 })
                        {
                            var bp = new Button { Text = "▶  Голосовое", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(47, 49, 54), ForeColor = Color.FromArgb(220, 221, 222), Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold), Size = new Size(170, 34), Location = new Point(LEFT, y), Cursor = Cursors.Hand };
                            bp.FlatAppearance.BorderSize = 0;
                            var ca = sm.audio;
                            bp.Click += (s, e) => MainForm.PlayVoiceClip(ca, bp);
                            holder.Controls.Add(bp); y += 40;
                        }
                        if (sm.video is { Length: > 0 })
                        {
                            try { var pl = new VideoCirclePlayer(sm.video, 180) { Location = new Point(LEFT, y) }; holder.Controls.Add(pl); y += 186; }
                            catch { }
                        }
                        if (sm.file is { Length: > 0 } && !string.IsNullOrWhiteSpace(sm.fname))
                        {
                            string fext = Path.GetExtension(sm.fname).TrimStart('.').ToLowerInvariant();
                            if (MediaPlayerForm.IsVideo(fext))
                            {
                                try
                                {
                                    int bw = Math.Min(msgWidth - 10, 280), bh = (int)(bw * 1.2);
                                    var vp = new InlineVideoPlayer(sm.file, sm.fname, bw, bh) { Location = new Point(LEFT, y) };
                                    holder.Controls.Add(vp); y += bh + 6;
                                }
                                catch { }
                            }
                            else
                            {
                                var card = MainForm.BuildFileCard(sm.file, sm.fname, isMe, msgWidth - 10, id, false);
                                card.Location = new Point(LEFT, y);
                                holder.Controls.Add(card); y += card.Height + 6;
                            }
                        }
                    }

                    // GIF-сообщение: "gif:<url>" — анимированная картинка.
                    if (text.StartsWith("gif:", StringComparison.OrdinalIgnoreCase))
                    {
                        var ph = new Panel { Location = new Point(LEFT, y), Size = new Size(220, 160), BackColor = Color.FromArgb(40, 42, 46) };
                        holder.Controls.Add(ph);
                        _ = LoadServerGifAsync(ph, text.Substring(4));
                        y += 166;
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        var body = MainForm.MakeSelectableText(text, bubbleBg,
                            Color.FromArgb(235, 236, 240), new Font("Segoe UI", 10.5f),
                            msgWidth - 10);
                        body.Location = new Point(LEFT, y);
                        holder.Controls.Add(body);
                        y += body.Height + 6;
                    }

                    // ── Реакции (как в ЛС): чипы «эмодзи N», клик — поставить/снять,
                    // «＋» — добавить новую через пикер. ─────────────────────────
                    try
                    {
                        var reacts = ReactionsRepository.ForMessage(id, ReactionsRepository.Scope.Server, _me);
                        if (reacts.Count > 0)
                        {
                            int rx = LEFT;
                            var cntFontR = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
                            foreach (var re in reacts)
                            {
                                // ЦВЕТНОЙ чип, как в ЛС: эмодзи — картинка Twemoji
                                // (GDI рисует эмодзи монохромными «контурами»), счётчик — текст.
                                const int emSz = 20;
                                var reImg = EmojiRender.Get(re.Emoji, emSz);
                                string cntTxt = re.Count.ToString();
                                int txtW = TextRenderer.MeasureText(cntTxt, cntFontR).Width;
                                int imgW = reImg?.Width ?? emSz;
                                var chipR = new Panel
                                {
                                    Size = new Size(8 + imgW + 4 + txtW + 7, 28),
                                    BackColor = re.Mine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(64, 68, 75),
                                    Location = new Point(rx, y),
                                    Cursor = Cursors.Hand
                                };
                                string emoC = re.Emoji; int midC = id;
                                var capCnt = cntTxt; var capImgW = imgW;
                                chipR.Paint += (s, e) =>
                                {
                                    var g = e.Graphics;
                                    var im = EmojiRender.Get(emoC, emSz);
                                    if (im != null) g.DrawImage(im, 8, (chipR.Height - im.Height) / 2);
                                    else using (var f0 = new Font("Segoe UI Emoji", 11f))
                                        g.DrawString(emoC, f0, Brushes.White, 6, 4);
                                    TextRenderer.DrawText(g, capCnt, cntFontR,
                                        new Point(8 + capImgW + 4, (chipR.Height - 16) / 2), Color.White);
                                };
                                Action<string> onLoad = em =>
                                {
                                    if (em != emoC || chipR.IsDisposed) return;
                                    try { chipR.BeginInvoke(new Action(() => { try { chipR.Invalidate(); } catch { } })); } catch { }
                                };
                                EmojiRender.Loaded += onLoad;
                                chipR.Disposed += (s, e) => { try { EmojiRender.Loaded -= onLoad; } catch { } };
                                MainForm.RoundCorners(chipR, 8);
                                chipR.Click += (s, e) =>
                                {
                                    System.Threading.Tasks.Task.Run(() =>
                                    {
                                        try { ReactionsRepository.Toggle(midC, ReactionsRepository.Scope.Server, _me, emoC); } catch { }
                                        if (IsDisposed || !IsHandleCreated) return;
                                        try { BeginInvoke(new Action(() => { _renderedKey = null; _renderedSig = null; LoadMessages(); })); } catch { }
                                    });
                                };
                                holder.Controls.Add(chipR);
                                chipR.BringToFront();
                                rx += chipR.Width + 6;
                            }
                            var addR = new Label
                            {
                                Text = "＋",
                                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                                AutoSize = true,
                                ForeColor = Color.FromArgb(200, 202, 208),
                                BackColor = Color.FromArgb(64, 68, 75),
                                Padding = new Padding(7, 3, 7, 3),
                                Location = new Point(rx, y),
                                Cursor = Cursors.Hand
                            };
                            int midA = id;
                            addR.Click += (s, e) =>
                            {
                                string emo = EmojiPickerForm.Pick(this, Cursor.Position);
                                if (string.IsNullOrWhiteSpace(emo)) return;
                                System.Threading.Tasks.Task.Run(() =>
                                {
                                    try { ReactionsRepository.Toggle(midA, ReactionsRepository.Scope.Server, _me, emo); } catch { }
                                    if (IsDisposed || !IsHandleCreated) return;
                                    try { BeginInvoke(new Action(() => { _renderedKey = null; _renderedSig = null; LoadMessages(); })); } catch { }
                                });
                            };
                            holder.Controls.Add(addR);
                            addR.BringToFront();
                        }
                    }
                    catch { }

                    AttachServerMsgMenu(holder, head, id, senderId, text, nm);
                    _msgControls[id] = holder;
                    _pnlMessages.Controls.Add(holder);
                }
                _lastMsgCount = dt.Rows.Count;
                _pnlMessages.ResumeLayout();
                _pnlMessages.ScrollControlIntoView(_pnlMessages.Controls.Count > 0 ? _pnlMessages.Controls[_pnlMessages.Controls.Count - 1] : null);
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        /// <summary>Прокручивает к исходному сообщению (по клику на цитату) и подсвечивает.</summary>
        private void ScrollToServerMessage(int id)
        {
            if (!_msgControls.TryGetValue(id, out var ctrl) || ctrl.IsDisposed) return;
            try
            {
                _pnlMessages.ScrollControlIntoView(ctrl);
                var orig = ctrl.BackColor;
                ctrl.BackColor = Color.FromArgb(60, 90, 130);
                var t = new System.Windows.Forms.Timer { Interval = 900 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!ctrl.IsDisposed) ctrl.BackColor = orig; };
                t.Start();
            }
            catch { }
        }

        /// <summary>Контекстное меню сообщения канала: ответить/переслать/копировать/
        /// редактировать/удалить.</summary>
        private void AttachServerMsgMenu(Panel holder, Control header, int id, int senderId, string text, string senderName = "")
        {
            bool isMine = senderId == _me;
            bool isGif = text.StartsWith("gif:", StringComparison.OrdinalIgnoreCase);

            // Метаданные для пакетных операций (пересылка/удаление выбранных).
            _srvMsgMeta[id] = (senderName ?? "", text ?? "", senderId);

            var menu = new ContextMenuStrip { BackColor = Color.FromArgb(24, 25, 28), ForeColor = Color.FromArgb(220, 221, 222) };

            // ── Выбрать (множественное выделение) ────────────────────────
            menu.Items.Add("☑  Выбрать", null, (s, e) =>
            {
                if (!_srvSelectMode) EnterSrvSelect();
                ToggleSrvSelect(id);
            });
            menu.Items.Add(new ToolStripSeparator());

            // ── Реакция (как в ЛС) ───────────────────────────────────────
            menu.Items.Add("😀  Реакция", null, (s, e) =>
            {
                string emo = EmojiPickerForm.Pick(this, Cursor.Position);
                if (string.IsNullOrWhiteSpace(emo)) return;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { ReactionsRepository.Toggle(id, ReactionsRepository.Scope.Server, _me, emo); } catch { }
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => { _renderedKey = null; _renderedSig = null; LoadMessages(); })); } catch { }
                });
            });
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("↩  Ответить", null, (s, e) => BeginServerReply(id, text));

            // Переслать в личку/группу: кладём текст в буфер главного окна —
            // дальше выбираешь диалог в ЛС и жмёшь «Отправить».
            if (!isGif)
                menu.Items.Add("↪  Переслать в ЛС/группу…", null, (s, e) =>
                {
                    var mf = MainForm.Current;
                    if (mf == null || mf.IsDisposed) { MessageBox.Show("Главное окно закрыто."); return; }
                    mf.BeginForwardExternal(senderName, text, id);
                    try { mf.Activate(); mf.BringToFront(); } catch { }
                });

            // Переслать в другой текстовый канал этого сервера.
            var fwd = new ToolStripMenuItem("↪  Переслать в…");
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT id,name FROM server_channels WHERE server_id=@s AND type='text' ORDER BY position,id", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int chId = Convert.ToInt32(r["id"]);
                    string chName = r["name"].ToString();
                    fwd.DropDownItems.Add("# " + chName, null, (s, e) => ForwardServerMessage(chId, id, senderName, text));
                }
            }
            catch { }
            if (fwd.DropDownItems.Count > 0) menu.Items.Add(fwd);

            if (!isGif)
                menu.Items.Add("📋  Копировать", null, (s, e) => { try { Clipboard.SetText(text); } catch { } });

            if (isMine && !isGif)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("✏  Редактировать", null, (s, e) => EditServerMessage(id, text));
            }

            // Удалить — ТОЛЬКО своё (чужие не удаляем даже с правами управления).
            if (isMine)
            {
                var del = new ToolStripMenuItem("🗑  Удалить") { ForeColor = Color.FromArgb(240, 71, 71) };
                del.Click += (s, e) => DeleteServerMessage(id);
                menu.Items.Add(del);
            }

            void Show(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
                else if (e.Button == MouseButtons.Left && _srvSelectMode) ToggleSrvSelect(id);
            }
            holder.MouseClick += Show;
            header.MouseClick += Show;
            // У выделяемого текста — наше меню вместо родного.
            foreach (Control c in holder.Controls)
                if (c is TextBox tb) tb.ContextMenuStrip = menu;
                else c.MouseClick += Show;

            // Кружок выделения у правого края (как в Telegram).
            if (_srvSelectMode)
            {
                bool sel = _srvSelected.Contains(id);
                var mark = new Label
                {
                    Text = sel ? "✔" : "○",
                    AutoSize = false,
                    Size = new Size(30, 30),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                    ForeColor = sel ? Color.FromArgb(59, 165, 93) : Color.FromArgb(150, 152, 158),
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand
                };
                mark.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleSrvSelect(id); };
                holder.Controls.Add(mark);
                mark.BringToFront();
                // Ставим кружок к правому краю чата (бабл авторазмерный — ждём layout).
                void Place(object s, EventArgs e)
                {
                    int w = Math.Max(holder.Width, _pnlMessages.ClientSize.Width - 40);
                    mark.Location = new Point(w - 36, Math.Max(6, (holder.Height - 30) / 2));
                }
                holder.Resize += Place;
                Place(null, null);
                if (sel) holder.BackColor = Color.FromArgb(58, 62, 70);
            }
        }

        private void ForwardServerMessage(int channelId, int srcMsgId, string senderName, string text)
        {
            try
            {
                ForwardHelper.Forward(2, srcMsgId, senderName, text, 2, _me, channelId);
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, channelId, "server");
                if (channelId == _channelId) { _renderedKey = null; _renderedSig = null; LoadMessages(); }
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void EditServerMessage(int id, string oldText)
        {
            string nt = Prompt("Изменить сообщение", oldText);
            if (nt == null) return;
            nt = nt.Trim();
            if (nt.Length == 0) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE server_messages SET text=@t WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(nt));
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server");
                LoadMessages();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        // ════════════════════════════════════════════════════════════════
        //  МНОЖЕСТВЕННОЕ ВЫДЕЛЕНИЕ В КАНАЛЕ (как в ЛС)
        // ════════════════════════════════════════════════════════════════
        private bool _srvSelectMode;
        private readonly HashSet<int> _srvSelected = new HashSet<int>();
        private readonly Dictionary<int, (string sender, string text, int senderId)> _srvMsgMeta
            = new Dictionary<int, (string, string, int)>();
        private Panel _srvSelectBar;
        private Label _srvSelectInfo;

        // ── Плашка «Пересылка N сообщений» (как в ЛС) над полем ввода ─────
        private Panel _srvFwdBar;
        private Label _srvFwdInfo;

        /// <summary>Показывает/прячет плашку пересылки в зависимости от того,
        /// есть ли в главном окне ожидающая пересылка.</summary>
        private void UpdateForwardNotice()
        {
            var mf = MainForm.Current;
            bool pending = mf != null && !mf.IsDisposed && mf.HasPendingForward;
            if (!pending) { if (_srvFwdBar != null) _srvFwdBar.Visible = false; return; }

            if (_srvFwdBar == null || _srvFwdBar.IsDisposed)
            {
                _srvFwdBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(47, 49, 54), Visible = false };
                _srvFwdInfo = new Label
                {
                    Dock = DockStyle.Fill,
                    ForeColor = Color.FromArgb(250, 166, 26),
                    Font = new Font("Segoe UI", 9f),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0)
                };
                var bx = new Button { Dock = DockStyle.Right, Width = 34, Text = "✕", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(47, 49, 54), ForeColor = Color.White, Cursor = Cursors.Hand };
                bx.FlatAppearance.BorderSize = 0;
                bx.Click += (s2, e2) =>
                {
                    try { MainForm.Current?.ConsumePendingForwards(); } catch { }   // сброс буфера
                    _srvFwdBar.Visible = false;
                };
                _srvFwdBar.Controls.Add(_srvFwdInfo);
                _srvFwdBar.Controls.Add(bx);
                var host = _pnlMessages.Parent;
                host.Controls.Add(_srvFwdBar);
                _srvFwdBar.BringToFront();
            }
            _srvFwdInfo.Text = "↪ Пересылка: нажмите «Отправить», чтобы переслать сообщения в этот канал";
            _srvFwdBar.Visible = _channelId > 0;
            _srvFwdBar.BringToFront();
        }

        private void EnsureSrvSelectBar()
        {
            if (_srvSelectBar != null && !_srvSelectBar.IsDisposed) return;
            _srvSelectBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.FromArgb(47, 49, 54), Visible = false };
            _srvSelectInfo = new Label
            {
                Dock = DockStyle.Left, Width = 160, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Padding = new Padding(12, 0, 0, 0), Text = "Выбрано: 0"
            };
            Button Mk(string t, Color fg)
            {
                var b = new Button { Text = t, Dock = DockStyle.Right, Width = 150, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(54, 57, 63), ForeColor = fg, Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0;
                return b;
            }
            var bCancel = Mk("Отмена", Color.FromArgb(200, 202, 208));
            var bDel = Mk("🗑  Удалить свои", Color.FromArgb(240, 71, 71));
            var bFwd = Mk("↪  Переслать", Color.FromArgb(88, 170, 255));
            bCancel.Click += (s, e) => ExitSrvSelect();
            bDel.Click += (s, e) => DeleteSrvSelected();
            bFwd.Click += (s, e) => ForwardSrvSelected();
            _srvSelectBar.Controls.Add(_srvSelectInfo);
            _srvSelectBar.Controls.Add(bCancel);
            _srvSelectBar.Controls.Add(bDel);
            _srvSelectBar.Controls.Add(bFwd);
            var host = _pnlMessages.Parent;
            host.Controls.Add(_srvSelectBar);
            _srvSelectBar.BringToFront();
        }

        private void EnterSrvSelect()
        {
            EnsureSrvSelectBar();
            _srvSelectMode = true;
            _srvSelected.Clear();
            _srvSelectBar.Visible = true;
            UpdateSrvSelectBar();
            _renderedKey = null; _renderedSig = null;   // форсим перерисовку с кружками
            LoadMessages();
        }

        private void ExitSrvSelect()
        {
            _srvSelectMode = false;
            _srvSelected.Clear();
            if (_srvSelectBar != null) _srvSelectBar.Visible = false;
            _renderedKey = null; _renderedSig = null;
            LoadMessages();
        }

        private void ToggleSrvSelect(int id)
        {
            if (id <= 0) return;
            if (!_srvSelected.Add(id)) _srvSelected.Remove(id);
            UpdateSrvSelectBar();
            _renderedKey = null; _renderedSig = null;
            LoadMessages();
        }

        private void UpdateSrvSelectBar()
        {
            if (_srvSelectInfo != null) _srvSelectInfo.Text = $"Выбрано: {_srvSelected.Count}";
        }

        private void DeleteSrvSelected()
        {
            // Удаляем ТОЛЬКО свои сообщения из выбранных.
            var mineIds = new List<int>();
            foreach (int id in _srvSelected)
                if (_srvMsgMeta.TryGetValue(id, out var m) && m.senderId == _me) mineIds.Add(id);
            if (mineIds.Count == 0) { MessageBox.Show("Среди выбранных нет ваших сообщений — чужие удалять нельзя."); return; }
            if (MessageBox.Show($"Удалить свои сообщения ({mineIds.Count})? Это нельзя отменить.",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                foreach (int id in mineIds)
                {
                    using var cmd = new MySqlCommand("DELETE FROM server_messages WHERE id=@id AND sender_id=@me", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@me", _me);
                    cmd.ExecuteNonQuery();
                }
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server");
            }
            catch (Exception ex) { ShowDbError(ex); }
            ExitSrvSelect();
        }

        private void ForwardSrvSelected()
        {
            if (_srvSelected.Count == 0) { ExitSrvSelect(); return; }
            var ids = new List<int>(_srvSelected);
            ids.Sort();
            var batch = new List<(string sender, string text, int id)>();
            foreach (int id in ids)
                if (_srvMsgMeta.TryGetValue(id, out var m))
                    batch.Add((m.sender, m.text, id));
            _srvSelectMode = false;
            _srvSelected.Clear();
            if (_srvSelectBar != null) _srvSelectBar.Visible = false;
            _renderedKey = null; _renderedSig = null;
            LoadMessages();
            if (batch.Count == 0) return;

            var mf = MainForm.Current;
            if (mf == null || mf.IsDisposed) { MessageBox.Show("Главное окно закрыто."); return; }
            mf.BeginForwardExternalBatch(batch);
            try { mf.Activate(); mf.BringToFront(); } catch { }
        }

        private void DeleteServerMessage(int id)
        {
            if (MessageBox.Show("Удалить сообщение?", "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("DELETE FROM server_messages WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server");
                LoadMessages();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        /// <summary>Качает GIF по url и вставляет анимированную картинку в плейсхолдер.</summary>
        private async System.Threading.Tasks.Task LoadServerGifAsync(Panel placeholder, string url)
        {
            byte[] data;
            try { data = await GiphyClient.DownloadAsync(url); }
            catch { return; }
            if (data == null || data.Length == 0 || placeholder.IsDisposed) return;
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    if (placeholder.IsDisposed) return;
                    var pb = AnimatedGif.Create(data, 260, 200);
                    pb.Location = new Point(0, 0);
                    placeholder.Size = pb.Size;
                    placeholder.Controls.Add(pb);
                }));
            }
            catch { }
        }

        private void SendChannelMessage()
        {
            if (_channelId <= 0) return;

            // Пересылка из ЛС/группы В КАНАЛ СЕРВЕРА: если в главном окне начата
            // пересылка — «Отправить» здесь шлёт пересылаемые сообщения в канал.
            var mf = MainForm.Current;
            if (mf != null && !mf.IsDisposed && mf.HasPendingForward)
            {
                foreach (var (sndr, txt, srcScope, srcId) in mf.ConsumePendingForwards())
                {
                    try { ForwardHelper.Forward(srcScope, srcId, sndr, txt, 2, _me, _channelId); }
                    catch (Exception ex) { ShowDbError(ex); return; }
                }
                try { WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server"); } catch { }
                if (_srvFwdBar != null) _srvFwdBar.Visible = false;
                _renderedKey = null; _renderedSig = null;
                LoadMessages();
                return;
            }

            string text = _txtInput.Text.Trim();

            // Ожидающее вложение (перетащенный/выбранный файл) — уходит именно сейчас,
            // по «Отправить»; текст, если есть, идёт подписью к нему.
            if (_chPendingImg != null || _chPendingFile != null)
            {
                var img = _chPendingImg; var file = _chPendingFile; var fn = _chPendingFileName;
                ClearChannelPending();
                _txtInput.Clear();
                if (img != null) SendChannelMedia(img, null, null, null, null);
                else SendChannelMedia(null, null, null, file, fn);
                if (!string.IsNullOrEmpty(text)) SendChannelRaw(text);
                return;
            }

            if (string.IsNullOrEmpty(text)) return;
            _txtInput.Clear();
            SendChannelRaw(text);
        }

        // ── Автоподсказка @упоминаний ───────────────────────────────────
        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            // Если открыта подсказка — стрелки/Enter/Tab/Esc управляют ею.
            if (_mentionPopup != null && _mentionPopup.Visible && _mentionList.Items.Count > 0)
            {
                if (e.KeyCode == Keys.Down) { _mentionList.SelectedIndex = Math.Min(_mentionList.SelectedIndex + 1, _mentionList.Items.Count - 1); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Up) { _mentionList.SelectedIndex = Math.Max(_mentionList.SelectedIndex - 1, 0); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab) { AcceptMention(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Escape) { HideMentionPopup(); e.SuppressKeyPress = true; return; }
            }
            // Enter — отправить; Shift+Enter — перенос строки (AcceptsReturn его
            // вставит сам, мы лишь НЕ перехватываем).
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendChannelMessage(); }
        }

        /// <summary>Находит активный токен @… у курсора и показывает/обновляет подсказку.</summary>
        private void UpdateMentionPopup()
        {
            string text = _txtInput.Text;
            int caret = _txtInput.SelectionStart;
            int at = -1;
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '@') { at = i; break; }
                if (char.IsWhiteSpace(c)) break;  // токен прерван пробелом
            }
            if (at < 0) { HideMentionPopup(); return; }

            string partial = text.Substring(at + 1, caret - at - 1).ToLowerInvariant();
            _mentionAtPos = at;
            BuildMentionItems(partial);
            if (_mentionItems.Count == 0) { HideMentionPopup(); return; }
            ShowMentionPopup();
        }

        private void BuildMentionItems(string partial)
        {
            _mentionItems.Clear();
            void Add(string token, string display, string desc)
            {
                if (string.IsNullOrEmpty(partial)
                    || token.ToLowerInvariant().Contains(partial)
                    || display.ToLowerInvariant().Contains(partial))
                    _mentionItems.Add((token, display, desc));
            }

            Add("everyone", "@everyone", "Оповестить всех участников канала");
            Add("here", "@here", "Оповестить тех, кто сейчас в сети");

            try
            {
                using var conn = DBHelper.OpenConnection();
                // Роли сервера.
                using (var cmd = new MySqlCommand("SELECT name FROM server_roles WHERE server_id=@s ORDER BY position,id", conn))
                {
                    cmd.Parameters.AddWithValue("@s", _serverId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string rn = r["name"].ToString();
                        if (!string.IsNullOrWhiteSpace(rn)) Add(rn, "@" + rn, "Оповестить роль");
                    }
                }
                // Участники.
                using (var cmd = new MySqlCommand(
                    "SELECT u.login, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm " +
                    "FROM server_members m JOIN users u ON u.id=m.user_id WHERE m.server_id=@s", conn))
                {
                    cmd.Parameters.AddWithValue("@s", _serverId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string login = r["login"].ToString();
                        string nm = r["nm"].ToString().Trim();
                        if (string.IsNullOrWhiteSpace(login)) continue;
                        string disp = string.IsNullOrWhiteSpace(nm) ? "@" + login : $"@{login} ({nm})";
                        Add(login, disp, "Участник");
                    }
                }
            }
            catch { }
        }

        private void BuildMentionPopupControl()
        {
            if (_mentionPopup != null) return;
            _mentionList = new NoFocusListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f),
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 34
            };
            _mentionList.DrawItem += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= _mentionItems.Count) return;
                var it = _mentionItems[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var bg = new SolidBrush(sel ? Color.FromArgb(59, 80, 120) : Color.FromArgb(32, 34, 37)))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                using var fMain = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
                using var fDesc = new Font("Segoe UI", 8f);
                e.Graphics.DrawString(it.display, fMain, Brushes.White, e.Bounds.Left + 8, e.Bounds.Top + 3);
                e.Graphics.DrawString(it.desc, fDesc, new SolidBrush(Color.FromArgb(150, 152, 158)), e.Bounds.Left + 8, e.Bounds.Top + 18);
            };
            // ЛКМ по пункту — сразу выбираем элемент под курсором и подставляем.
            // (Окно подсказки не активируется, поэтому надёжнее работать по MouseDown
            // с хит-тестом, а не по событию Click.)
            _mentionList.MouseDown += (s, e) =>
            {
                int idx = _mentionList.IndexFromPoint(e.Location);
                if (idx >= 0 && idx < _mentionList.Items.Count)
                {
                    _mentionList.SelectedIndex = idx;
                    AcceptMention();
                }
            };

            _mentionPopup = new NoActivateForm
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(32, 34, 37),
                Size = new Size(340, 180)
            };
            _mentionPopup.Controls.Add(_mentionList);
        }

        private void ShowMentionPopup()
        {
            BuildMentionPopupControl();
            _mentionList.BeginUpdate();
            _mentionList.Items.Clear();
            foreach (var _ in _mentionItems) _mentionList.Items.Add("");
            _mentionList.EndUpdate();
            if (_mentionList.Items.Count > 0) _mentionList.SelectedIndex = 0;

            int rows = Math.Min(5, _mentionItems.Count);
            _mentionPopup.Height = rows * _mentionList.ItemHeight + 4;

            // Позиционируем над полем ввода.
            try
            {
                var p = _txtInput.PointToScreen(new Point(0, 0));
                _mentionPopup.Location = new Point(p.X, p.Y - _mentionPopup.Height - 2);
            }
            catch { }

            if (!_mentionPopup.Visible)
                _mentionPopup.Show(this); // не активируется (NoActivateForm) — фокус в поле ввода
        }

        /// <summary>Окно, которое не забирает фокус при показе (для подсказки @).</summary>
        private sealed class NoActivateForm : Form
        {
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */; return cp; }
            }
        }

        /// <summary>ListBox, который НЕ забирает фокус при клике — иначе клик по
        /// подсказке уводил фокус из поля ввода, срабатывал LostFocus и
        /// подсказка скрывалась раньше, чем успевала подставиться.</summary>
        private sealed class NoFocusListBox : ListBox
        {
            public NoFocusListBox() { SetStyle(ControlStyles.Selectable, false); }
        }

        private void HideMentionPopup()
        {
            _mentionAtPos = -1;
            if (_mentionPopup != null && _mentionPopup.Visible) _mentionPopup.Hide();
        }

        private void AcceptMention()
        {
            if (_mentionList == null || _mentionList.SelectedIndex < 0) return;
            // Позиция «@» могла сброситься (гонка с LostFocus) — восстановим по тексту.
            if (_mentionAtPos < 0)
            {
                int caret0 = Math.Min(_txtInput.SelectionStart, _txtInput.Text.Length);
                _mentionAtPos = _txtInput.Text.LastIndexOf('@', Math.Max(0, caret0 - 1));
                if (_mentionAtPos < 0) return;
            }
            var it = _mentionItems[_mentionList.SelectedIndex];
            int caret = _txtInput.SelectionStart;
            string text = _txtInput.Text;
            if (_mentionAtPos > text.Length) { HideMentionPopup(); return; }

            string before = text.Substring(0, _mentionAtPos);
            string after = caret <= text.Length ? text.Substring(caret) : "";
            string insert = "@" + it.token + " ";
            _txtInput.Text = before + insert + after;
            _txtInput.SelectionStart = (before + insert).Length;
            HideMentionPopup();
            _txtInput.Focus(); // возвращаем курсор в поле ввода после клика по подсказке
        }

        /// <summary>Записывает сообщение канала (текст или "gif:&lt;url&gt;") и рассылает.</summary>
        private void SendChannelRaw(string rawText)
        {
            if (string.IsNullOrEmpty(rawText) || _channelId <= 0) return;
            int replyId = _replyToId;
            try
            {
                using var conn = DBHelper.OpenConnection();
                MySqlCommand cmd;
                if (_replyColOk && replyId > 0)
                {
                    cmd = new MySqlCommand("INSERT INTO server_messages (channel_id, sender_id, text, reply_to_id) VALUES (@c,@s,@t,@r)", conn);
                    cmd.Parameters.AddWithValue("@r", replyId);
                }
                else
                {
                    cmd = new MySqlCommand("INSERT INTO server_messages (channel_id, sender_id, text) VALUES (@c,@s,@t)", conn);
                }
                cmd.Parameters.AddWithValue("@c", _channelId);
                cmd.Parameters.AddWithValue("@s", _me);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(rawText));
                cmd.ExecuteNonQuery();
                cmd.Dispose();
                CancelServerReply();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server");
                NotifyMentions(rawText);
                // Ответ через «Ответить» — уведомляем автора исходного сообщения.
                if (replyId > 0)
                {
                    try
                    {
                        using var q = new MySqlCommand("SELECT sender_id FROM server_messages WHERE id=@id", conn);
                        q.Parameters.AddWithValue("@id", replyId);
                        var o = q.ExecuteScalar();
                        int author = o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
                        if (author > 0 && author != _me)
                            WebSocketSignalingClient.Instance.SendMessage(
                                "reply", author, _channelId, $"{_serverId}|{_serverName}|{_channelName}");
                    }
                    catch { }
                }
                LoadMessages();
            }
            catch (MySqlException mex) when (mex.Number == 1054)
            {
                _replyColOk = false; // колонки reply_to_id нет — миграция не выполнена
                SendChannelRaw(rawText);
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        // ── Медиа в канале: вложения, голосовые, кружки ──────────────────
        private static void AddBlobParam(MySqlCommand cmd, string name, byte[] data)
        {
            var p = cmd.Parameters.Add(name, MySqlDbType.LongBlob);
            p.Value = (data != null && data.Length > 0) ? (object)data : DBNull.Value;
        }

        /// <summary>Отправляет медиа-сообщение в канал (картинка/голос/видео/файл).</summary>
        private void SendChannelMedia(byte[] image, byte[] audio, byte[] video, byte[] file, string fileName)
        {
            if (_channelId <= 0) return;
            if (!MediaColumnsExist())
            {
                MessageBox.Show("Медиа в каналах недоступно: на сервере не выполнена миграция\n(scripts/server_media_migration.sql).", "PISMO");
                return;
            }
            int replyId = _replyToId;
            int channel = _channelId;
            string title = fileName ?? "медиа";

            // Окно с отменой + фоновая вставка (большой blob не вешает UI; INSERT
            // атомарен, поэтому Cancel прерывает до коммита — «хвостов» не остаётся).
            var dlg = new Form
            {
                Text = "Отправка файла", FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
                ClientSize = new Size(300, 188), BackColor = Color.FromArgb(40, 42, 46), ControlBox = false
            };
            double angle = 0; bool ok = false; bool cancelled = false; bool retryNoReply = false;
            string err = null; MySqlCommand activeCmd = null; MySqlConnection activeConn = null;
            var pic = new Panel { Size = new Size(72, 72), Location = new Point(114, 14), BackColor = Color.Transparent };
            pic.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(6, 6, 58, 58);
                using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 6);
                using var arc = new Pen(Color.FromArgb(88, 101, 242), 6);
                e.Graphics.DrawEllipse(track, rect);
                e.Graphics.DrawArc(arc, rect, (float)angle, 110);
            };
            var lbl = new Label { Text = "Отправка " + title, ForeColor = Color.FromArgb(220, 221, 222), TextAlign = ContentAlignment.MiddleCenter, Location = new Point(10, 92), Size = new Size(280, 46), Font = new Font("Segoe UI", 9f) };
            var btnCancel = new Button { Text = "Отмена", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.White, Size = new Size(120, 30), Location = new Point(90, 146), Cursor = Cursors.Hand };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) => { cancelled = true; btnCancel.Enabled = false; btnCancel.Text = "Отмена…"; try { activeCmd?.Cancel(); } catch { } try { var c = activeConn; c?.Close(); } catch { } };
            dlg.Controls.Add(pic); dlg.Controls.Add(lbl); dlg.Controls.Add(btnCancel);
            var anim = new System.Windows.Forms.Timer { Interval = 60 };
            anim.Tick += (s, e) => { angle = (angle + 24) % 360; pic.Invalidate(); };
            dlg.Shown += (s, e) => anim.Start();
            dlg.FormClosed += (s, e) => { try { anim.Stop(); anim.Dispose(); } catch { } };

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    activeConn = conn;
                    try { using var to = new MySqlCommand("SET SESSION net_read_timeout=600, net_write_timeout=600, wait_timeout=600", conn); to.ExecuteNonQuery(); } catch { }
                    string cols = "channel_id, sender_id, text, image_data, audio_data, video_data, file_data, file_name";
                    string vals = "@c,@s,@t,@img,@aud,@vid,@fd,@fn";
                    bool withReply = _replyColOk && replyId > 0;
                    if (withReply) { cols += ", reply_to_id"; vals += ",@r"; }
                    using var cmd = new MySqlCommand($"INSERT INTO server_messages ({cols}) VALUES ({vals})", conn);
                    cmd.Parameters.AddWithValue("@c", channel);
                    cmd.Parameters.AddWithValue("@s", _me);
                    cmd.Parameters.AddWithValue("@t", Crypto.Enc(""));
                    AddBlobParam(cmd, "@img", image);
                    AddBlobParam(cmd, "@aud", audio);
                    AddBlobParam(cmd, "@vid", video);
                    AddBlobParam(cmd, "@fd", file);
                    cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);
                    if (withReply) cmd.Parameters.AddWithValue("@r", replyId);
                    cmd.CommandTimeout = 600;
                    activeCmd = cmd;
                    cmd.ExecuteNonQuery();
                    activeCmd = null;
                    ok = true;
                }
                catch (MySqlException mex) when (mex.Number == 1054)
                {
                    if (_replyColOk) { _replyColOk = false; retryNoReply = true; }
                    else { _mediaColPresent = false; err = "Медиа в каналах недоступно: не выполнена миграция server_messages."; }
                }
                catch (Exception ex) { if (!cancelled) err = ex.Message; }
                finally { try { dlg.BeginInvoke(() => dlg.Close()); } catch { } }
            });

            dlg.ShowDialog(this);

            if (cancelled) return;
            if (retryNoReply) { SendChannelMedia(image, audio, video, file, fileName); return; }
            if (!ok) { if (err != null) MessageBox.Show(err, "PISMO"); return; }

            CancelServerReply();
            WebSocketSignalingClient.Instance.SendMessage("new_message", 0, channel, "server");
            LoadMessages();
        }

        /// <summary>Прикрепить файл (или картинку) в канал по пути (перетаскивание
        /// из проводника). НЕ отправляет сразу — кладёт в «ожидание» и показывает
        /// превью; уходит только по кнопке «Отправить».</summary>
        private void AttachChannelFileByPath(string path)
        {
            if (_channelId <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.LongLength > 30L * 1024 * 1024)
                {
                    MessageBox.Show("Файл слишком большой (>30 МБ).", "PISMO");
                    return;
                }
                string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                bool isImg = ext is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp";
                StageChannelAttachment(bytes, Path.GetFileName(path), isImg);
            }
            catch (Exception ex) { MessageBox.Show("Не удалось прикрепить файл: " + ex.Message, "PISMO"); }
        }

        /// <summary>Кладёт вложение в ожидание и показывает полоску-превью над вводом.
        /// Второе вложение заменяет первое (как в мессенджере — одно за раз).</summary>
        private void StageChannelAttachment(byte[] bytes, string fileName, bool isImg)
        {
            if (bytes == null || bytes.Length == 0) return;
            _chPendingImg = isImg ? bytes : null;
            _chPendingFile = isImg ? null : bytes;
            _chPendingFileName = isImg ? null : fileName;

            if (_chPreview != null && _chPreviewLbl != null)
            {
                string sizeTxt = bytes.Length >= 1024 * 1024
                    ? $"{bytes.Length / 1024.0 / 1024.0:0.0} МБ"
                    : $"{Math.Max(1, bytes.Length / 1024)} КБ";

                // Убираем старую иконку/миниатюру (кроме постоянных label и крестика).
                for (int i = _chPreview.Controls.Count - 1; i >= 0; i--)
                {
                    var c = _chPreview.Controls[i];
                    if (c != _chPreviewLbl && !(c is Button)) { _chPreview.Controls.Remove(c); c.Dispose(); }
                }

                // Иконка слева: миниатюра для картинки, иначе цветной бейдж типа файла
                // (общий с мессенджером MainForm.MakeFileIcon).
                Control icon = null;
                string nm = isImg ? "Изображение" : fileName;
                if (isImg)
                {
                    try
                    {
                        var pb = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(54, 54), Location = new Point(12, 7) };
                        using var ms = new MemoryStream(bytes);
                        pb.Image = Image.FromStream(ms);
                        icon = pb;
                    }
                    catch { }
                }
                if (icon == null)
                {
                    icon = MainForm.MakeFileIcon(isImg ? "image.png" : fileName);
                    icon.Location = new Point(12, 7);
                }
                _chPreview.Controls.Add(icon);
                icon.SendToBack();

                _chPreviewLbl.Text = $"{nm}  ({sizeTxt}) — нажмите «Отправить»";
                _chPreview.Height = 68;
                _chPreview.Visible = true;
                UpdateBottomHeight();
            }
            try { _txtInput?.Focus(); } catch { }
        }

        /// <summary>Сбрасывает ожидающее вложение и прячет полоску-превью.</summary>
        private void ClearChannelPending()
        {
            _chPendingImg = null;
            _chPendingFile = null;
            _chPendingFileName = null;
            if (_chPreview != null)
            {
                _chPreview.Visible = false;
                _chPreview.Height = 0;
            }
            UpdateBottomHeight();
        }

        /// <summary>Включает перетаскивание файлов на контрол → превью в канале
        /// (без немедленной отправки).</summary>
        private void EnableChannelFileDrop(Control c)
        {
            if (c == null) return;
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
                e.Effect = (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (s, e) =>
            {
                try
                {
                    // Только первый файл (одно вложение за раз, как в мессенджере).
                    if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                        AttachChannelFileByPath(files[0]);
                }
                catch { }
            };
        }

        private void AttachChannelFile(bool imageOnly)
        {
            if (_channelId <= 0) return;
            using var ofd = new OpenFileDialog
            {
                Title = imageOnly ? "Выберите изображение" : "Выберите файл",
                Filter = imageOnly ? "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" : "Все файлы|*.*"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var bytes = File.ReadAllBytes(ofd.FileName);
                if (bytes.LongLength > 30L * 1024 * 1024)
                {
                    MessageBox.Show("Файл слишком большой (>30 МБ).", "PISMO");
                    return;
                }
                string ext = Path.GetExtension(ofd.FileName).TrimStart('.').ToLowerInvariant();
                bool isImg = ext is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp";
                // Как в мессенджере: показываем превью, отправка — по «Отправить».
                StageChannelAttachment(bytes, Path.GetFileName(ofd.FileName), imageOnly || isImg);
            }
            catch (Exception ex) { MessageBox.Show("Не удалось прикрепить файл: " + ex.Message, "PISMO"); }
        }

        private void ChVoice_MouseDown(object sender, MouseEventArgs e)
        {
            if (_channelId <= 0) return;
            try
            {
                _chAudioStream = new MemoryStream();
                _chWaveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
                if (DeviceSettings.MicrophoneIndex >= 0 && DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                    _chWaveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;
                _chWaveWriter = new WaveFileWriter(_chAudioStream, _chWaveIn.WaveFormat);
                _chWaveIn.DataAvailable += (s, ev) => { try { _chWaveWriter?.Write(ev.Buffer, 0, ev.BytesRecorded); } catch { } };
                _chWaveIn.StartRecording();
                if (_btnChVoice != null) { _btnChVoice.ForeColor = Color.FromArgb(240, 71, 71); _btnChVoice.Text = "🔴"; }
            }
            catch (Exception ex) { MessageBox.Show("Нет доступа к микрофону: " + ex.Message, "PISMO"); }
        }

        private void ChVoice_MouseUp(object sender, MouseEventArgs e)
        {
            if (_chWaveIn == null) return;
            try
            {
                _chWaveIn.StopRecording();
                _chWaveIn.Dispose(); _chWaveIn = null;
                _chWaveWriter.Flush();
                byte[] audio = _chAudioStream.ToArray();
                _chWaveWriter.Dispose(); _chWaveWriter = null;
                _chAudioStream.Dispose(); _chAudioStream = null;
                if (_btnChVoice != null) { _btnChVoice.ForeColor = Color.FromArgb(220, 221, 222); _btnChVoice.Text = "🎤"; }
                if (audio.Length > 4000) SendChannelMedia(null, audio, null, null, null);
            }
            catch { }
        }

        private void RecordChannelCircle()
        {
            if (_channelId <= 0) return;
            using var dlg = new VideoCircleRecordForm();
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ResultVideoData != null)
                SendChannelMedia(null, null, dlg.ResultVideoData, null, null);
        }

        /// <summary>Открывает поиск GIF и отправляет выбранную как "gif:&lt;url&gt;".</summary>
        private void OpenServerGifPicker()
        {
            if (_channelId <= 0) return;
            var picker = new GifPickerForm();
            picker.GifSelected += url => { if (!string.IsNullOrWhiteSpace(url)) SendChannelRaw("gif:" + url); };
            picker.Show(this);
        }

        /// <summary>Шлёт WS-уведомление «mention» каждому упомянутому участнику.</summary>
        private void NotifyMentions(string text)
        {
            try
            {
                foreach (int uid in ResolveMentionedUserIds(text))
                    if (uid != _me)
                        WebSocketSignalingClient.Instance.SendMessage(
                            "mention", uid, _channelId, $"{_serverId}|{_serverName}|{_channelName}");
            }
            catch { }
        }

        /// <summary>Разбирает @упоминания: @все/@all/@everyone, @роль, @логин.</summary>
        private HashSet<int> ResolveMentionedUserIds(string text)
        {
            var ids = new HashSet<int>();
            if (string.IsNullOrEmpty(text) || !text.Contains('@')) return ids;

            var tokens = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"@([^\s@]+)"))
                tokens.Add(m.Groups[1].Value.ToLowerInvariant().Trim('.', ',', '!', '?', ':'));
            if (tokens.Count == 0) return ids;

            bool all = tokens.Contains("все") || tokens.Contains("all")
                       || tokens.Contains("everyone") || tokens.Contains("here") || tokens.Contains("здесь");

            try
            {
                using var conn = DBHelper.OpenConnection();
                // Участники: uid, логин, роль.
                using (var cmd = new MySqlCommand(
                    "SELECT m.user_id, u.login, IFNULL(LOWER(r.name),'') AS rname " +
                    "FROM server_members m JOIN users u ON u.id=m.user_id " +
                    "LEFT JOIN server_roles r ON r.id=m.role_id WHERE m.server_id=@s", conn))
                {
                    cmd.Parameters.AddWithValue("@s", _serverId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int uid = Convert.ToInt32(r["user_id"]);
                        string login = r["login"].ToString().ToLowerInvariant();
                        string rname = r["rname"].ToString();
                        if (all) { ids.Add(uid); continue; }
                        if (tokens.Contains(login)) ids.Add(uid);
                        else if (!string.IsNullOrEmpty(rname) && tokens.Contains(rname)) ids.Add(uid);
                    }
                }
            }
            catch { }
            return ids;
        }

        /// <summary>Упоминают ли меня в этом тексте (для подсветки).</summary>
        private bool MentionsMe(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            if (t.Contains("@все") || t.Contains("@all") || t.Contains("@everyone")) return true;
            if (!string.IsNullOrEmpty(_myLogin) && t.Contains("@" + _myLogin.ToLowerInvariant())) return true;
            if (!string.IsNullOrEmpty(_myRoleName) && t.Contains("@" + _myRoleName.ToLowerInvariant())) return true;
            return false;
        }

        // ── Участники ───────────────────────────────────────────────────
        private void LoadMembers()
        {
            _pnlMembers.Controls.Clear();
            _memberButtons.Clear();
            _pnlMembers.Controls.Add(MakeHeader("Участники"));
            try
            {
                var roles = GetRoles(); // (id, name) для подменю «Выдать роль»

                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.user_id, m.role_id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login, s.owner_id, " +
                    "r.name AS rname, r.color AS rcolor " +
                    "FROM server_members m JOIN users u ON u.id=m.user_id JOIN servers s ON s.id=m.server_id " +
                    "LEFT JOIN server_roles r ON r.id=m.role_id " +
                    "WHERE m.server_id=@s ORDER BY (m.user_id=s.owner_id) DESC, u.login", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int uid = Convert.ToInt32(r["user_id"]);
                    bool owner = Convert.ToInt32(r["owner_id"]) == uid;
                    string nm = r["nm"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"].ToString();
                    string rname = r["rname"] == DBNull.Value ? "" : r["rname"].ToString();

                    var b = MakeSideButton((owner ? "👑 " : "") + nm + (string.IsNullOrEmpty(rname) ? "" : $"  [{rname}]"),
                        Color.FromArgb(54, 57, 63));
                    if (!string.IsNullOrEmpty(rname) && r["rcolor"] != DBNull.Value)
                        try { b.ForeColor = ColorTranslator.FromHtml(r["rcolor"].ToString()); } catch { }

                    bool canManageThis = uid != _me && !owner && (_canKick || _canBan || _canManage);
                    if (canManageThis)
                    {
                        var menu = new ContextMenuStrip();
                        if (_canManage)
                        {
                            var roleItem = new ToolStripMenuItem("Выдать роль");
                            foreach (var (rid, rn) in roles)
                            {
                                int ridCap = rid;
                                roleItem.DropDownItems.Add(rn, null, (s, e) => AssignRole(uid, ridCap));
                            }
                            roleItem.DropDownItems.Add(new ToolStripSeparator());
                            roleItem.DropDownItems.Add("— Снять роль —", null, (s, e) => AssignRole(uid, null));
                            menu.Items.Add(roleItem);
                        }
                        if (_canKick) menu.Items.Add("Выгнать", null, (s, e) => KickMember(uid, false));
                        if (_canBan) menu.Items.Add("Забанить", null, (s, e) => KickMember(uid, true));
                        if (menu.Items.Count > 0) { b.ContextMenuStrip = menu; b.Text += "  ⋮"; }
                    }
                    AttachPresenceDot(b, uid, nm);
                    _pnlMembers.Controls.Add(b);
                }
                RefreshMemberPresence();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private List<(int id, string name)> GetRoles()
        {
            var list = new List<(int, string)>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT id,name FROM server_roles WHERE server_id=@s ORDER BY position,id", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((Convert.ToInt32(r["id"]), r["name"].ToString()));
            }
            catch { }
            return list;
        }

        private void AssignRole(int uid, int? roleId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE server_members SET role_id=@r WHERE server_id=@s AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@r", (object)roleId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@s", _serverId);
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.ExecuteNonQuery();
                LoadMembers();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void ToggleServerMute()
        {
            try
            {
                _serverMuted = !_serverMuted;
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE server_members SET muted_notifs=@m WHERE server_id=@s AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@m", _serverMuted ? 1 : 0);
                cmd.Parameters.AddWithValue("@s", _serverId);
                cmd.Parameters.AddWithValue("@u", _me);
                cmd.ExecuteNonQuery();
                LoadChannels();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        /// <summary>Окно управления ролями: список + создание роли с правами и цветом.</summary>
        private void ManageRoles()
        {
            using var f = new Form
            {
                Text = "Роли сервера",
                ClientSize = new Size(420, 500),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(47, 49, 54),
                MaximizeBox = false, MinimizeBox = false
            };
            var list = new ListBox { Location = new Point(12, 12), Size = new Size(396, 150), BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            void Reload() { list.Items.Clear(); foreach (var (id, n) in GetRoles()) list.Items.Add($"{id}: {n}"); }
            Reload();

            var lblN = new Label { Text = "Название роли:", ForeColor = Color.White, Location = new Point(12, 176), AutoSize = true };
            var txtN = new TextBox { Location = new Point(12, 196), Size = new Size(260, 24), BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var lblC = new Label { Text = "Цвет:", ForeColor = Color.White, Location = new Point(284, 176), AutoSize = true };
            var txtC = new TextBox { Location = new Point(284, 196), Size = new Size(94, 24), Text = "#3BA55D", BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            // Живое превью выбранного цвета справа от поля ввода.
            var swPreview = new Panel { Location = new Point(384, 196), Size = new Size(24, 24), BorderStyle = BorderStyle.FixedSingle };
            void SyncPreview()
            {
                try { swPreview.BackColor = ColorTranslator.FromHtml(txtC.Text.Trim()); }
                catch { swPreview.BackColor = Color.FromArgb(40, 42, 46); }
            }
            txtC.TextChanged += (s, e) => SyncPreview();
            SyncPreview();

            // Палитра готовых цветов (как в Discord) — чтобы не вписывать HEX руками.
            var lblPal = new Label { Text = "Палитра:", ForeColor = Color.White, Location = new Point(12, 230), AutoSize = true };
            string[] palette =
            {
                "#1ABC9C", "#2ECC71", "#3498DB", "#9B59B6", "#E91E63", "#F1C40F",
                "#E67E22", "#E74C3C", "#95A5A6", "#607D8B", "#3BA55D", "#5865F2",
            };
            var swatches = new List<Panel>();
            int px = 12, py = 250;
            foreach (var hex in palette)
            {
                var sw = new Panel { Location = new Point(px, py), Size = new Size(26, 26), BackColor = ColorTranslator.FromHtml(hex), Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle };
                string hexCap = hex;
                sw.Click += (s, e) => { txtC.Text = hexCap; };
                swatches.Add(sw);
                px += 32;
                if (px + 26 > 408) { px = 12; py += 32; }
            }
            // Кнопка системного выбора цвета — на СВОЕЙ строке под палитрой,
            // чтобы не наезжать на чекбоксы.
            var btnMore = new Button { Text = "🎨 Ещё…", Location = new Point(12, 286), Size = new Size(90, 26), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.White, Cursor = Cursors.Hand };
            btnMore.FlatAppearance.BorderSize = 0;
            btnMore.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true };
                try { cd.Color = ColorTranslator.FromHtml(txtC.Text.Trim()); } catch { }
                if (cd.ShowDialog(f) == DialogResult.OK)
                    txtC.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
            };

            var cbBan = new CheckBox { Text = "Банить", ForeColor = Color.White, Location = new Point(12, 324), AutoSize = true };
            var cbKick = new CheckBox { Text = "Выгонять", ForeColor = Color.White, Location = new Point(120, 324), AutoSize = true };
            var cbMute = new CheckBox { Text = "Мьютить", ForeColor = Color.White, Location = new Point(232, 324), AutoSize = true };
            var cbManage = new CheckBox { Text = "Управление (каналы/роли)", ForeColor = Color.White, Location = new Point(12, 352), AutoSize = true };

            // Какая роль сейчас редактируется (null — режим создания новой).
            int? editingId = null;
            var btnSave = new Button { Text = "Сохранить", Location = new Point(138, 388), Size = new Size(120, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White, Enabled = false };

            void ResetForm()
            {
                editingId = null;
                txtN.Clear(); txtC.Text = "#3BA55D";
                cbBan.Checked = cbKick.Checked = cbMute.Checked = cbManage.Checked = false;
                btnSave.Enabled = false;
                try { list.ClearSelected(); } catch { }
            }

            // Выбор роли в списке → подгружаем её поля для редактирования.
            list.SelectedIndexChanged += (s, e) =>
            {
                if (list.SelectedItem == null) { return; }
                string sel = list.SelectedItem.ToString();
                int rid = int.Parse(sel.Substring(0, sel.IndexOf(':')));
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "SELECT name,color,can_ban,can_kick,can_mute,can_manage FROM server_roles WHERE id=@r", conn);
                    cmd.Parameters.AddWithValue("@r", rid);
                    using var rd = cmd.ExecuteReader();
                    if (rd.Read())
                    {
                        editingId = rid;
                        txtN.Text = rd["name"].ToString();
                        txtC.Text = rd["color"] == DBNull.Value ? "#99AAB5" : rd["color"].ToString();
                        cbBan.Checked = Convert.ToInt32(rd["can_ban"]) != 0;
                        cbKick.Checked = Convert.ToInt32(rd["can_kick"]) != 0;
                        cbMute.Checked = Convert.ToInt32(rd["can_mute"]) != 0;
                        cbManage.Checked = Convert.ToInt32(rd["can_manage"]) != 0;
                        btnSave.Enabled = true;
                    }
                }
                catch (Exception ex) { ShowDbError(ex); }
            };

            var btnCreate = new Button { Text = "Создать роль", Location = new Point(12, 388), Size = new Size(120, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(59, 165, 93), ForeColor = Color.White };
            btnCreate.Click += (s, e) =>
            {
                string n = txtN.Text.Trim();
                if (string.IsNullOrEmpty(n)) return;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "INSERT INTO server_roles (server_id,name,color,can_ban,can_kick,can_mute,can_manage,position) " +
                        "VALUES (@s,@n,@c,@b,@k,@mu,@mg,10)", conn);
                    cmd.Parameters.AddWithValue("@s", _serverId);
                    cmd.Parameters.AddWithValue("@n", n);
                    cmd.Parameters.AddWithValue("@c", string.IsNullOrWhiteSpace(txtC.Text) ? "#99AAB5" : txtC.Text.Trim());
                    cmd.Parameters.AddWithValue("@b", cbBan.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@k", cbKick.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@mu", cbMute.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@mg", cbManage.Checked ? 1 : 0);
                    cmd.ExecuteNonQuery();
                    ResetForm(); Reload();
                }
                catch (Exception ex) { ShowDbError(ex); }
            };

            btnSave.Click += (s, e) =>
            {
                if (editingId == null) return;
                string n = txtN.Text.Trim();
                if (string.IsNullOrEmpty(n)) return;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "UPDATE server_roles SET name=@n,color=@c,can_ban=@b,can_kick=@k,can_mute=@mu,can_manage=@mg " +
                        "WHERE id=@r", conn);
                    cmd.Parameters.AddWithValue("@n", n);
                    cmd.Parameters.AddWithValue("@c", string.IsNullOrWhiteSpace(txtC.Text) ? "#99AAB5" : txtC.Text.Trim());
                    cmd.Parameters.AddWithValue("@b", cbBan.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@k", cbKick.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@mu", cbMute.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@mg", cbManage.Checked ? 1 : 0);
                    cmd.Parameters.AddWithValue("@r", editingId.Value);
                    cmd.ExecuteNonQuery();
                    ResetForm(); Reload();
                }
                catch (Exception ex) { ShowDbError(ex); }
            };

            var btnDel = new Button { Text = "Удалить", Location = new Point(264, 388), Size = new Size(120, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(240, 71, 71), ForeColor = Color.White };
            btnDel.Click += (s, e) =>
            {
                if (list.SelectedItem == null) return;
                string sel = list.SelectedItem.ToString();
                int rid = int.Parse(sel.Substring(0, sel.IndexOf(':')));
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using (var c1 = new MySqlCommand("UPDATE server_members SET role_id=NULL WHERE role_id=@r", conn)) { c1.Parameters.AddWithValue("@r", rid); c1.ExecuteNonQuery(); }
                    using (var c2 = new MySqlCommand("DELETE FROM server_roles WHERE id=@r", conn)) { c2.Parameters.AddWithValue("@r", rid); c2.ExecuteNonQuery(); }
                    ResetForm(); Reload();
                }
                catch (Exception ex) { ShowDbError(ex); }
            };

            var ctrls = new List<Control> { list, lblN, txtN, lblC, txtC, swPreview, lblPal, btnMore,
                cbBan, cbKick, cbMute, cbManage, btnCreate, btnSave, btnDel };
            ctrls.AddRange(swatches);
            f.Controls.AddRange(ctrls.ToArray());
            f.ShowDialog(this);
            LoadMembers();
        }

        private void KickMember(int uid, bool ban)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var del = new MySqlCommand("DELETE FROM server_members WHERE server_id=@s AND user_id=@u", conn))
                { del.Parameters.AddWithValue("@s", _serverId); del.Parameters.AddWithValue("@u", uid); del.ExecuteNonQuery(); }
                if (ban)
                {
                    using var b = new MySqlCommand("INSERT IGNORE INTO server_bans (server_id,user_id) VALUES (@s,@u)", conn);
                    b.Parameters.AddWithValue("@s", _serverId); b.Parameters.AddWithValue("@u", uid); b.ExecuteNonQuery();
                }
                LoadMembers();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        // ── Вспомогательное ─────────────────────────────────────────────
        private Label MakeHeader(string t) => new Label
        {
            Text = t.ToUpper(),
            ForeColor = Color.FromArgb(150, 152, 158),
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(2, 2, 0, 6)
        };

        /// <summary>Ширина строки внутри FlowLayoutPanel = вся доступная ширина панели
        /// (за вычетом внутренних отступов, полей строки и вертикального скроллбара),
        /// чтобы кнопки каналов и строки участников не были «короче» границы.</summary>
        private static void StretchRow(FlowLayoutPanel host, Control c)
        {
            if (c == null) return;
            int avail = host.ClientSize.Width - host.Padding.Horizontal - c.Margin.Horizontal;
            if (avail > 20 && c.Width != avail) c.Width = avail;
        }

        private static void StretchRows(FlowLayoutPanel host)
        {
            foreach (Control c in host.Controls) StretchRow(host, c);
        }

        private Button MakeSideButton(string text, Color back)
        {
            var b = new Button
            {
                Text = text,
                Width = 160,
                Height = 34,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = Color.FromArgb(220, 221, 222),
                Margin = new Padding(0, 0, 0, 4),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private static void ShowDbError(Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SERVERS] " + ex.Message);
            if (ex.Message.Contains("server_") || ex.Message.Contains("doesn't exist"))
                MessageBox.Show("Похоже, не выполнена миграция серверов (scripts/servers_migration.sql).",
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private string Prompt(string title, string def = "")
        {
            using var f = new Form
            {
                Text = title,
                ClientSize = new Size(340, 120),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(47, 49, 54),
                MaximizeBox = false,
                MinimizeBox = false
            };
            var tb = new TextBox { Location = new Point(14, 20), Size = new Size(312, 26), Text = def, BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11f) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(160, 70), Size = new Size(76, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(248, 70), Size = new Size(78, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.White };
            f.Controls.AddRange(new Control[] { tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }
}
