using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using NAudio.Wave;

namespace PISMO
{
    public partial class MainForm : Form
    {
        // ── Состояние ──────────────────────────────────────────────────────
        private int _currentChatPartnerId = -1;
        private string _currentChatPartnerName = "";
        private byte[] _pendingImageBytes = null;
        private PendingAttachment _pendingAttach = null;

        // Групповые чаты
        private int _currentGroupId = -1;
        private string _currentGroupName = "";
        private readonly List<Panel> _groupPanels = new();

        private int _lastGroupMsgCount = 0;

        // Список карточек в сайдбаре (для обновления бейджей без перезагрузки)
        private readonly List<Panel> _userPanels = new();

        // Кнопка GIF-поиска в строке ввода (создаётся в коде, не в Designer)
        private Button btnGif;

        // Окно серверов (как в Discord)
        private ServersForm _serversForm;

        // Polling: обнаружение новых сообщений
        private System.Windows.Forms.Timer _pollTimer;
        private int _lastMsgCount = 0;
        private bool _pollBusy = false;
        private readonly Dictionary<int, int> _prevUnread = new();

        // Кеш метаданных переписки в памяти: при повторном открытии чата сообщения
        // показываются мгновенно из кеша, а свежие подгружаются в фоне (не вешая UI).
        private readonly Dictionary<int, DataTable> _msgMetaCache = new();   // partnerId -> meta
        private readonly Dictionary<int, (bool iBlocked, bool theyBlocked)> _blockCache = new();
        private readonly Dictionary<int, DataTable> _groupMetaCache = new(); // groupId -> meta
        // Что сейчас отрисовано в панели: чтобы не перерисовывать чат заново, если
        // данные не изменились (открытие рисует из кэша, потом из БД — второй раз
        // полное пересоздание пузырей лишнее и тормозит).
        private string _renderedChatKey;
        private string _renderedChatSig;

        /// <summary>Дешёвая «подпись» переписки (без создания контролов) для сравнения.</summary>
        internal static string SigOf(DataTable dt)
        {
            if (dt == null) return "0";
            bool hasEdit = dt.Columns.Contains("edited_at");
            bool hasDel = dt.Columns.Contains("is_deleted");
            bool hasText = dt.Columns.Contains("text");
            bool hasRead = dt.Columns.Contains("is_read");
            var sb = new System.Text.StringBuilder();
            sb.Append(dt.Rows.Count);
            foreach (DataRow r in dt.Rows)
            {
                sb.Append('|').Append(r["id"]);
                if (hasEdit && r["edited_at"] != DBNull.Value) sb.Append('e').Append(r["edited_at"]);
                if (hasDel && r["is_deleted"] != DBNull.Value && Convert.ToBoolean(r["is_deleted"])) sb.Append('d');
                if (hasRead && r["is_read"] != DBNull.Value && Convert.ToBoolean(r["is_read"])) sb.Append('r');
                if (hasText && r["text"] != DBNull.Value) sb.Append('t').Append(r["text"].ToString().Length);
            }
            return sb.ToString();
        }

        // Голосовые сообщения
        private WaveInEvent _waveIn;
        private MemoryStream _audioStream;
        private WaveFileWriter _waveWriter;
        private WaveOutEvent _waveOut;

        // ════════════════════════════════════════════════════════════════
        //  ИНИЦИАЛИЗАЦИЯ
        // ════════════════════════════════════════════════════════════════
        public MainForm()
        {
            // TURN больше не используется — звонки идут через LiveKit (SFU).
            // (Раньше здесь запускался генератор TURN-кредов, всплывавший окном.)
            DeviceSettings.Load();
            InitializeComponent();

            MediaCache.Init();
            SetupPolling();
            BuildSidebarSearch();
            this.Load += MainForm_Load;
        }

        private TextBox _convSearch;

        /// <summary>Поле поиска чатов над списком диалогов в боковой панели.</summary>
        private void BuildSidebarSearch()
        {
            var host = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(32, 34, 37), Padding = new Padding(8, 5, 8, 5) };
            _convSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                PlaceholderText = "Поиск чатов…"
            };
            _convSearch.TextChanged += (s, e) => FilterConversations(_convSearch.Text);
            host.Controls.Add(_convSearch);
            pnlSidebar.Controls.Add(host);

            // Док обрабатывается от ВЫСШЕГО индекса к низшему. Чтобы список (Fill) занял
            // остаток (а не всю высоту с наложением шапки/поиска), задаём порядок явно:
            //   шапка (3, к верхнему краю) -> поиск (2, под шапкой) -> футер (1, низ) -> список (0, Fill последним).
            pnlSidebar.Controls.SetChildIndex(pnlUserList, 0);
            try { pnlSidebar.Controls.SetChildIndex(pnlSidebarFooter, 1); } catch { }
            pnlSidebar.Controls.SetChildIndex(host, 2);
            pnlSidebar.Controls.SetChildIndex(pnlSidebarHeader, 3);
        }

        /// <summary>Фильтрует список диалогов/групп по подстроке (имя — в AccessibleName карточки).</summary>
        private void FilterConversations(string q)
        {
            q = (q ?? "").Trim().ToLowerInvariant();
            pnlUserList.SuspendLayout();
            foreach (var p in _userPanels)
                p.Visible = q.Length == 0 || (p.AccessibleName ?? "").ToLowerInvariant().Contains(q);
            foreach (var p in _groupPanels)
                p.Visible = q.Length == 0 || (p.AccessibleName ?? "").ToLowerInvariant().Contains(q);
            pnlUserList.ResumeLayout();
        }

        private void TrayMenuOpen_Click(object sender, EventArgs e)
        {
            // Показать окно и восстановить, если свернуто
            if (!this.Visible) this.Show();
            if (this.WindowState == FormWindowState.Minimized)
                this.WindowState = FormWindowState.Normal;
            try { this.Activate(); } catch { }
        }

        private void TrayMenuExit_Click(object sender, EventArgs e)
        {
            // Корректно остановим процессы и выйдем
            try { _pollTimer?.Stop(); } catch { }
            try { _trayIcon.Visible = false; } catch { }
            Application.Exit();
        }

        private void pnlUserList_Resize(object sender, EventArgs e)
        {
            // Подгоняем ширину карточек и бейджей при изменении размеров панели
            try
            {
                foreach (Control ctrl in pnlUserList.Controls)
                {
                    if (ctrl is Panel pnl)
                    {
                        pnl.Width = CardWidth;
                        foreach (Control c in pnl.Controls)
                        {
                            // бейджи с красным фоном — корректируем позицию
                            if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                                lb.Location = new Point(Math.Max(0, pnl.Width - 34), 22);

                            // подписи и превью — корректируем ширину
                            if (c is Label lbl && lbl.AutoEllipsis)
                                lbl.Size = new Size(Math.Max(40, pnl.Width - 90), lbl.Height);
                        }
                    }
                }
            }
            catch { }
        }

        private void pnlChatHeader_Paint(object sender, PaintEventArgs e)
        {
            // Рисуем тонкую разделительную линию внизу заголовка чата
            var p = sender as Panel ?? pnlChatHeader;
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            // КРИТИЧНО для пуш-уведомлений: NotifyIcon.ShowBalloonTip() молча
            // ничего не показывает, если у иконки не задан Icon. Раньше _trayIcon
            // создавался без иконки — отсюда «уведомления не приходят». Назначаем
            // иконку приложения и трею, и самой форме.
            try
            {
                var icoPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pismo.ico");
                System.Drawing.Icon appIcon = System.IO.File.Exists(icoPath)
                    ? new System.Drawing.Icon(icoPath)
                    : System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null)
                {
                    this.Icon = appIcon;
                    if (_trayIcon != null) { _trayIcon.Icon = appIcon; _trayIcon.Visible = true; }
                }
            }
            catch { /* иконка не критична для работы */ }

            lblCurrentUser.Text = UserSession.UserName;

            // Аватарки: перерисовываем карточки, когда аватар загрузился в фоне;
            // двойной клик по своему имени — сменить аватар.
            AvatarStore.AvatarLoaded += uid =>
            {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => InvalidateAvatarFor(uid))); }
                catch { }
            };
            try
            {
                lblCurrentUser.Cursor = Cursors.Hand;
                lblCurrentUser.DoubleClick -= LblCurrentUser_DoubleClick;
                lblCurrentUser.DoubleClick += LblCurrentUser_DoubleClick;
            }
            catch { }

            // Кружок-аватар возле имени аккаунта (видно сразу где менять аватар).
            try
            {
                pnlMyAvatar.Paint += PnlMyAvatar_Paint;
                pnlMyAvatar.Click += (s, e) => OpenProfile();
                var ttAv = new ToolTip();
                ttAv.SetToolTip(pnlMyAvatar, "Нажмите, чтобы открыть профиль");
                AvatarStore.EnsureLoaded(UserSession.EffectiveId);
            }
            catch { }

            // Кнопка GIF в строке ввода (слева от «Отправить»).
            try
            {
                btnGif = new Button
                {
                    Text = "GIF",
                    Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                    Size = new Size(44, 40),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(64, 68, 75),
                    ForeColor = Color.FromArgb(220, 221, 222),
                    Cursor = Cursors.Hand
                };
                btnGif.FlatAppearance.BorderSize = 0;
                btnGif.Click += (s, e) => OpenGifPicker();
                pnlInputBar.Controls.Add(btnGif);
                btnGif.BringToFront();
                pnlInputBar_Resize(this, EventArgs.Empty);
            }
            catch { }

            // Кнопка «Серверы» (как в Discord) в шапке сайдбара.
            try
            {
                var btnServers = new Button
                {
                    Text = "🗗",
                    Font = new Font("Segoe UI", 13F),
                    Dock = DockStyle.Right,
                    Size = new Size(36, 48),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(185, 187, 190),
                    Cursor = Cursors.Hand
                };
                btnServers.FlatAppearance.BorderSize = 0;
                btnServers.Click += (s, e) =>
                {
                    if (_serversForm == null || _serversForm.IsDisposed)
                    {
                        _serversForm = new ServersForm();
                        _serversForm.FormClosed += (a, b) => _serversForm = null;
                        _serversForm.Show(this);
                    }
                    else _serversForm.Activate();
                };
                pnlSidebarHeader.Controls.Add(btnServers);
                btnServers.BringToFront();
                new ToolTip().SetToolTip(btnServers, "Серверы");
            }
            catch { }

            InitMessageActions();
            StartPresence();

            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();
            pnlInputBar_Resize(this, EventArgs.Empty);

            _ = WebSocketSignalingClient.Instance.ConnectAsync(UserSession.EffectiveId);
            WebSocketSignalingClient.Instance.OnMessageReceived += (type, senderId, sessionId, payload) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (type == "new_message")
                        {
                            // Пришло событие — открытый чат перезагружаем напрямую
                            // (без сверки COUNT: при медленной удалённой БД она
                            // промахивалась и сообщение появлялось лишь при переоткрытии).
                            if (_currentGroupId >= 0) LoadGroupMessages();
                            else if (_currentChatPartnerId >= 0) LoadMessages();
                            PollTick(null, null); // непрочитанные/бейджи
                        }
                        else if (type == "auth_error")
                        {
                            System.Diagnostics.Debug.WriteLine("[WS] register отклонён сервером (auth_error)");
                        }
                        else if (type == "read")
                        {
                            // Собеседник (senderId) прочитал мои сообщения — обновим галочки.
                            if (senderId == _currentChatPartnerId) LoadMessages();
                        }
                        else if (type == "mention")
                        {
                            // payload: serverId|serverName|channelName
                            HandleMentionNotification(payload);
                        }
                        else if (type == "profile_updated")
                        {
                            // sessionId = uid пользователя, обновившего профиль.
                            int uid2 = sessionId;
                            if (uid2 > 0)
                            {
                                AvatarStore.Invalidate(uid2);
                                AvatarStore.EnsureLoaded(uid2);
                                InvalidateAvatarFor(uid2);
                                try { LoadConversations(); } catch { }
                                try { if (_activeCall != null && !_activeCall.IsDisposed) _activeCall.OnRemoteProfileUpdated(uid2); } catch { }
                            }
                        }
                    });
                }
                catch { }
            };
        }

        /// <summary>Трей-уведомление об упоминании на сервере (если сервер не
        /// заглушён). payload: "serverId|serverName|channelName".</summary>
        private void HandleMentionNotification(string payload)
        {
            try
            {
                var parts = (payload ?? "").Split('|');
                if (parts.Length < 3) return;
                if (!int.TryParse(parts[0], out int serverId)) return;

                // Уважаем заглушение сервера.
                bool muted = false;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "SELECT muted_notifs FROM server_members WHERE server_id=@s AND user_id=@u", conn);
                    cmd.Parameters.AddWithValue("@s", serverId);
                    cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                    var o = cmd.ExecuteScalar();
                    muted = o != null && o != DBNull.Value && Convert.ToInt32(o) == 1;
                }
                catch { }
                if (muted) return;

                try { Sounds.Message(); } catch { }
                if (_trayIcon != null && _trayIcon.Icon != null)
                    _trayIcon.ShowBalloonTip(4000, "PISMO — упоминание",
                        $"Вас упомянули: {parts[1]} · #{parts[2]}", ToolTipIcon.Info);
                try { FlashWindow(this.Handle, true); } catch { }
            }
            catch { }
        }

        /// <summary>Перерисовать аватар в карточке пользователя uid (после
        /// фоновой загрузки аватара).</summary>
        private void InvalidateAvatarFor(int uid)
        {
            foreach (var p in _userPanels)
            {
                if (p.Tag is int id && id == uid)
                    foreach (Control c in p.Controls)
                        if (c is Panel av) { try { av.Invalidate(); } catch { } }
            }
            // Свой кружок-аватар в футере.
            if (uid == UserSession.EffectiveId)
                try { pnlMyAvatar?.Invalidate(); } catch { }
        }

        /// <summary>Рисует круглый аватар текущего пользователя в футере сайдбара.</summary>
        private void PnlMyAvatar_Paint(object sender, PaintEventArgs e)
        {
            int uid = UserSession.EffectiveId;
            int size = Math.Min(pnlMyAvatar.Width, pnlMyAvatar.Height) - 2;
            int x = (pnlMyAvatar.Width - size) / 2, y = (pnlMyAvatar.Height - size) / 2;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (!AvatarStore.DrawAvatar(e.Graphics, uid, x, y, size))
            {
                e.Graphics.FillEllipse(new SolidBrush(GetAvatarColor(uid)), x, y, size, size);
                string nm = UserSession.UserName ?? "";
                string letter = nm.Length > 0 ? nm[0].ToString().ToUpper() : "?";
                using var f = new Font("Segoe UI Black", 13f, FontStyle.Bold);
                var sz = e.Graphics.MeasureString(letter, f);
                e.Graphics.DrawString(letter, f, Brushes.White,
                    x + (size - sz.Width) / 2, y + (size - sz.Height) / 2);
            }
        }

        private void LblCurrentUser_DoubleClick(object sender, EventArgs e) => OpenProfile();

        /// <summary>Открывает окно редактирования профиля и обновляет шапку/аватар.</summary>
        private void OpenProfile()
        {
            using var pf = new ProfileForm(UserSession.EffectiveId);
            pf.ShowDialog(this);
            if (pf.Saved)
            {
                lblCurrentUser.Text = UserSession.UserName;
                AvatarStore.Invalidate(UserSession.EffectiveId);
                AvatarStore.EnsureLoaded(UserSession.EffectiveId);
                InvalidateAvatarFor(UserSession.EffectiveId);
                try { pnlMyAvatar?.Invalidate(); } catch { }
                try { LoadConversations(); } catch { }

                // Применяем изменения в активном звонке (имя+аватар) и сообщаем
                // другим клиентам, чтобы у них обновился аватар в чатах/звонке.
                try { if (_activeCall != null && !_activeCall.IsDisposed) _activeCall.ApplyMyProfileChanged(); } catch { }
                try { WebSocketSignalingClient.Instance.SendMessage("profile_updated", 0, UserSession.EffectiveId, ""); } catch { }
            }
        }

        /// <summary>Открывает окно поиска гифок (Giphy) и отправляет выбранную.</summary>
        private void OpenGifPicker()
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите чат или группу.", "PISMO");
                return;
            }
            var picker = new GifPickerForm();
            try
            {
                var loc = PointToScreen(new Point(pnlInputBar.Left, pnlInputBar.Top));
                picker.StartPosition = FormStartPosition.Manual;
                picker.Location = new Point(Math.Max(0, loc.X + 40), Math.Max(0, loc.Y - 540));
            }
            catch { }
            picker.GifSelected += url => _ = SendGifByUrlAsync(url);
            picker.Show(this);
        }

        private async System.Threading.Tasks.Task SendGifByUrlAsync(string url)
        {
            try
            {
                var bytes = await GiphyClient.DownloadAsync(url);
                if (bytes == null || bytes.Length == 0)
                {
                    MessageBox.Show("Не удалось загрузить гифку.", "PISMO");
                    return;
                }
                if (IsDisposed) return;
                // Отправляем как изображение (анимация определяется по содержимому GIF).
                if (_currentGroupId >= 0) SendGroupMessage("", bytes);
                else if (_currentChatPartnerId >= 0) SendMessage("", bytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки гифки: " + ex.Message, "PISMO");
            }
        }

        /// <summary>Выбор и загрузка своей аватарки (с уменьшением до 256px).</summary>
        private void UploadMyAvatar()
        {
            try
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Выберите аватар",
                    Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp"
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                byte[] data;
                using (var src = (Bitmap)Image.FromFile(dlg.FileName))
                {
                    // Квадрат по центру + уменьшение до 256 — компактно для БД.
                    int side = Math.Min(src.Width, src.Height);
                    int sx = (src.Width - side) / 2, sy = (src.Height - side) / 2;
                    int target = Math.Min(256, side);
                    using var dst = new Bitmap(target, target);
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, target, target),
                            new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                    }
                    using var ms = new MemoryStream();
                    dst.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    data = ms.ToArray();
                }

                int myId = UserSession.EffectiveId;
                if (AvatarStore.SaveMyAvatar(myId, data))
                {
                    InvalidateAvatarFor(myId);
                    MessageBox.Show("Аватар обновлён.", "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Не удалось сохранить аватар. Выполнена ли миграция avatar_data?",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки аватара: " + ex.Message, "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── Polling: таймер на 2.5 с ───────────────────────────────────
        private void SetupPolling()
        {
            // Реальное время обеспечивает WebSocket. Таймер — ФОЛБЭК: опрашивает БД
            // ТОЛЬКО когда WS не подключён (иначе тик почти бесплатный — проверка флага
            // и выход). Так нет постоянного лага при живом WS, но сообщения доходят и
            // при обрыве WS. Плюс ручная кнопка ↻.
            _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pollTimer.Tick += PollTick;
            _pollTimer.Start();
        }

        private void PollTick(object sender, EventArgs e)
        {
            if (_pollBusy) return;
            _pollBusy = true;

            // id видимых карточек собираем на UI-потоке (потоконебезопасно иначе).
            var ids = new List<int>();
            try { foreach (var p in _userPanels) if (p.Tag is int uid) ids.Add(uid); } catch { }

            // Все запросы — в фоне (не вешаем UI). Открытый чат перезагружаем ВСЕГДА
            // (skip-render не даст мигания, но обновит галочки «прочитано», которые
            // не меняют число сообщений). Плюс непрочитанные и статусы присутствия.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var unread = ReadUnreadCounts();
                    var presence = ReadPresence(ids);

                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (_currentGroupId >= 0) LoadGroupMessages();
                            else if (_currentChatPartnerId >= 0) LoadMessages();
                            if (unread != null) ApplyUnreadAndNotify(unread);
                            ApplyPresence(presence);
                        }
                        catch { }
                    }));
                }
                catch { }
                finally { _pollBusy = false; }
            });
        }

        // Применяет статусы присутствия и перерисовывает карточки ТОЛЬКО при изменении.
        private void ApplyPresence(Dictionary<int, int> fresh)
        {
            if (fresh == null) return;
            bool changed = fresh.Count != _presence.Count;
            if (!changed)
                foreach (var kv in fresh)
                    if (!_presence.TryGetValue(kv.Key, out int v) || v != kv.Value) { changed = true; break; }
            if (!changed) return;
            _presence.Clear();
            foreach (var kv in fresh) _presence[kv.Key] = kv.Value;
            InvalidateCardAvatars();
        }

        private int GetGroupMsgCount()
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM group_messages WHERE group_id=@g", conn);
            cmd.Parameters.AddWithValue("@g", _currentGroupId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private int GetMsgCount()
        {
            int myId = UserSession.EffectiveId;
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM messages WHERE (sender_id=@me AND receiver_id=@th) OR (sender_id=@th AND receiver_id=@me)",
                conn);
            cmd.Parameters.AddWithValue("@me", myId);
            cmd.Parameters.AddWithValue("@th", _currentChatPartnerId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // ── Чтение непрочитанных (DB, выполняется в фоне) ─────────────
        private Dictionary<int, int> ReadUnreadCounts()
        {
            int myId = UserSession.EffectiveId;
            var current = new Dictionary<int, int>();
            try
            {
                // Один запрос: непрочитанные по отправителям, СРАЗУ исключая блокировки
                // (раньше на каждого отправителя открывалось по 2 соединения к удалённой
                // БД — это и был периодический фриз и спам потоков).
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.sender_id, COUNT(*) AS cnt FROM messages m " +
                    "WHERE m.receiver_id=@me AND m.is_read=0 " +
                    "AND NOT EXISTS (SELECT 1 FROM user_blocks b WHERE " +
                    "   (b.blocker_id=@me AND b.blocked_id=m.sender_id) OR " +
                    "   (b.blocker_id=m.sender_id AND b.blocked_id=@me)) " +
                    "GROUP BY m.sender_id", conn);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    current[Convert.ToInt32(r["sender_id"])] = Convert.ToInt32(r["cnt"]);
            }
            catch { return null; }
            return current;
        }

        // ── Применение бейджей + уведомления (UI-поток) ───────────────
        private void ApplyUnreadAndNotify(Dictionary<int, int> current)
        {
            // Уведомления при росте счётчика.
            foreach (var kv in current)
            {
                int sid = kv.Key;
                int cnt = kv.Value;
                _prevUnread.TryGetValue(sid, out int prev);

                if (cnt > prev && sid != _currentChatPartnerId)
                    ShowNewMessageNotification(sid, cnt);
            }

            // Если непрочитанные не изменились с прошлого тика — НЕ трогаем UI вообще
            // (раньше каждые 2.5 c пересоздавались шрифты и перекладывались карточки —
            // отсюда периодический микро-фриз).
            bool changed = current.Count != _prevUnread.Count;
            if (!changed)
                foreach (var kv in current)
                    if (!_prevUnread.TryGetValue(kv.Key, out int pv) || pv != kv.Value) { changed = true; break; }

            _prevUnread.Clear();
            foreach (var kv in current) _prevUnread[kv.Key] = kv.Value;

            if (!changed) return;

            UpdateBadgesOnCards(current);

            int totalUnread = current.Values.Sum();
            string title = totalUnread > 0 ? $"● PISMO ({totalUnread} новых)" : "PISMO — Мессенджер";
            if (this.Text != title)
            {
                this.Text = title;
                _trayIcon.Text = title.Length > 63 ? title[..63] : title;
            }
        }

        private void ShowNewMessageNotification(int senderId, int unreadCount)
        {
            string senderName = GetNameFromCards(senderId);
            try { Sounds.Message(); } catch { }
            _trayIcon.ShowBalloonTip(
                4000,
                "PISMO — новое сообщение",
                $"{senderName}: {unreadCount} непрочитанных",
                ToolTipIcon.Info);

            if (!this.ContainsFocus)
                FlashWindow(this.Handle, true);
        }

        private string GetNameFromCards(int uid)
        {
            foreach (var p in _userPanels)
            {
                if (p.Tag is int id && id == uid)
                {
                    foreach (Control c in p.Controls)
                        if (c is Label lbl && lbl.Font.Bold && lbl.ForeColor == Color.FromArgb(220, 221, 222))
                            return lbl.Text;
                }
            }
            return $"Пользователь #{uid}";
        }

        private void UpdateBadgesOnCards(Dictionary<int, int> unread)
        {
            foreach (var pnl in _userPanels)
            {
                if (pnl.Tag is not int uid) continue;
                unread.TryGetValue(uid, out int cnt);

                Label badge = null;
                foreach (Control c in pnl.Controls)
                    if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                    { badge = lb; break; }

                if (cnt > 0 && badge == null)
                {
                    badge = new Label
                    {
                        Text = cnt > 9 ? "9+" : cnt.ToString(),
                        Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                        ForeColor = Color.White,
                        BackColor = Color.FromArgb(240, 71, 71),
                        Size = new Size(22, 18),
                        Location = new Point(pnl.Width - 34, 22),
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    pnl.Controls.Add(badge);
                    badge.BringToFront();
                }
                else if (cnt > 0 && badge != null)
                {
                    badge.Text = cnt > 9 ? "9+" : cnt.ToString();
                }
                else if (cnt == 0 && badge != null)
                {
                    pnl.Controls.Remove(badge);
                }

                var wantFont = cnt > 0 ? _cardFontBold : _cardFontNormal;
                foreach (Control c in pnl.Controls)
                    if (c is Label lbl && lbl.Font.Size >= 9 && !ReferenceEquals(lbl.Font, wantFont))
                        lbl.Font = wantFont; // кэшированные шрифты — без аллокаций каждый тик
            }
        }

        // Кэшированные шрифты карточек (раньше создавались заново на каждом тике).
        private static readonly Font _cardFontBold = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        private static readonly Font _cardFontNormal = new Font("Segoe UI", 10f);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        // ════════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА САЙДБАРА
        // ════════════════════════════════════════════════════════════════
        private void LoadConversations()
        {
            pnlUserList.Controls.Clear();
            _userPanels.Clear();
            _groupPanels.Clear();

            int myId = UserSession.EffectiveId;
            lblSidebarTitle.Text = UserSession.IsImpersonating
                ? $"💬 За: {UserSession.EffectiveName}"
                : "Личные сообщения";

            LoadGroups();

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = @"
                    SELECT u.id, u.Name, u.Surname, u.login,
                           MAX(m.created_at) AS last_time,
                           (SELECT m2.text FROM messages m2
                            WHERE (m2.sender_id = @me AND m2.receiver_id = u.id)
                               OR (m2.sender_id = u.id AND m2.receiver_id = @me)
                            ORDER BY m2.created_at DESC LIMIT 1) AS last_msg,
                           SUM(CASE WHEN m.sender_id=u.id
                                     AND m.receiver_id=@me
                                     AND m.is_read=0 THEN 1 ELSE 0 END) AS unread
                    FROM users u
                    LEFT JOIN messages m
                           ON (m.sender_id=@me AND m.receiver_id=u.id)
                           OR (m.sender_id=u.id AND m.receiver_id=@me)
                    WHERE u.id <> @me
                    GROUP BY u.id, u.Name, u.Surname, u.login
                    ORDER BY last_time DESC, u.Name ASC";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", myId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                foreach (DataRow row in dt.Rows)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string name = BuildName(row["Name"], row["Surname"], row["login"]);
                    string lastMsg = row["last_msg"] == DBNull.Value ? "" : Crypto.Dec(row["last_msg"].ToString());
                    int unread = row["unread"] == DBNull.Value ? 0 : Convert.ToInt32(row["unread"]);

                    AddUserCard(uid, name, lastMsg, unread);
                }
                if (_convSearch != null) FilterConversations(_convSearch.Text);
                try { PresenceTick(); } catch { } // разово обновить статусы под список
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки диалогов: " + ex.Message);
            }
        }

        private void LoadAllUsersForAdmin()
        {
            pnlUserList.Controls.Clear();
            _userPanels.Clear();
            _groupPanels.Clear();
            lblSidebarTitle.Text = "Все пользователи";

            LoadGroups();

            var lblHint = new Label
            {
                Text = "ЛКМ — написать  •  ПКМ — войти за пользователя",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(90, 93, 102),
                AutoSize = false,
                Width = pnlSidebar.Width - 12,
                Height = 30,
                Padding = new Padding(8, 6, 0, 0)
            };
            pnlUserList.Controls.Add(lblHint);

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "SELECT id, Name, Surname, login, role FROM users ORDER BY Name";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", UserSession.UserId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                foreach (DataRow row in dt.Rows)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string name = BuildName(row["Name"], row["Surname"], row["login"]);
                    string role = row["role"].ToString();
                    AddAdminUserCard(uid, name, role);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ГРУППОВЫЕ ЧАТЫ — ЗАГРУЗКА СПИСКА
        // ════════════════════════════════════════════════════════════════
        private void LoadGroups()
        {
            int myId = UserSession.EffectiveId;

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = @"
                    SELECT gc.id, gc.name, gc.avatar_color,
                           (SELECT gm2.text FROM group_messages gm2
                            WHERE gm2.group_id = gc.id
                            ORDER BY gm2.created_at DESC LIMIT 1) AS last_msg,
                           (SELECT MAX(gm3.created_at) FROM group_messages gm3
                            WHERE gm3.group_id = gc.id) AS last_time,
                           (SELECT COUNT(*) FROM group_members gmem2 WHERE gmem2.group_id = gc.id) AS member_count
                    FROM group_chats gc
                    JOIN group_members gmem ON gmem.group_id = gc.id AND gmem.user_id = @me
                    ORDER BY last_time DESC, gc.name ASC";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", myId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                if (dt.Rows.Count == 0) return;

                var lblHeader = new Label
                {
                    Text = "ГРУППЫ",
                    Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(114, 118, 125),
                    AutoSize = false,
                    Width = CardWidth,
                    Height = 24,
                    Padding = new Padding(10, 8, 0, 0)
                };
                pnlUserList.Controls.Add(lblHeader);

                foreach (DataRow row in dt.Rows)
                {
                    int gid = Convert.ToInt32(row["id"]);
                    string name = row["name"].ToString();
                    string lastMsg = row["last_msg"] == DBNull.Value ? "" : Crypto.Dec(row["last_msg"].ToString());
                    int members = Convert.ToInt32(row["member_count"]);
                    string colorHex = row["avatar_color"] == DBNull.Value ? "#5865F2" : row["avatar_color"].ToString();

                    AddGroupCard(gid, name, lastMsg, members, colorHex);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки групп: " + ex.Message);
            }
        }

        private void AddGroupCard(int gid, string name, string lastMsg, int memberCount, string colorHex)
        {
            Color avatarColor;
            try { avatarColor = ColorTranslator.FromHtml(colorHex); }
            catch { avatarColor = Color.FromArgb(88, 101, 242); }

            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 62,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = gid
            };

            var avatar = new Panel { Size = new Size(38, 38), Location = new Point(10, 12) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(new SolidBrush(avatarColor),
                    0, 0, avatar.Width - 1, avatar.Height - 1);
                string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "#";
                using var f = new Font("Segoe UI Black", 14f, FontStyle.Bold);
                var sz = e.Graphics.MeasureString(letter, f);
                e.Graphics.DrawString(letter, f, Brushes.White,
                    (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
            };

            var lblName = new Label
            {
                Text = "👥 " + name,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 10),
                Size = new Size(pnl.Width - 90, 20),
                AutoEllipsis = true
            };

            string preview = string.IsNullOrWhiteSpace(lastMsg)
                ? $"{memberCount} участник(ов)"
                : (lastMsg.Length > 42 ? lastMsg[..42] + "…" : lastMsg);

            var lblLast = new Label
            {
                Text = preview,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 32),
                Size = new Size(pnl.Width - 90, 18),
                AutoEllipsis = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);
            pnl.Controls.Add(lblLast);

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                56, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentGroupId == gid
                    ? Color.FromArgb(65, 68, 75) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);
            // ЛКМ открывает группу; ПКМ — только меню (иначе Panel/Label вызывают
            // Click и на правую кнопку, и чат открывался вместе с меню).
            pnl.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenGroup(gid, name); };

            // Контекстное меню: участники / добавление / выход из группы
            var ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add("👥 Участники группы", null, (s, ev) => ShowGroupMembers(gid, name));
            ctxMenu.Items.Add("➕ Добавить участников", null, (s, ev) => QuickAddMembers(gid, name));
            ctxMenu.Items.Add(new ToolStripSeparator());
            ctxMenu.Items.Add("🚪 Покинуть группу", null, (s, ev) => LeaveGroup(gid, name));
            pnl.ContextMenuStrip = ctxMenu;

            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenGroup(gid, name); };
                c.ContextMenuStrip = ctxMenu;
            }

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _groupPanels.Add(pnl);
        }

        private void QuickAddMembers(int gid, string groupName)
        {
            using var dlg = new AddGroupMembersForm(gid);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.AddedCount > 0)
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                if (_currentGroupId == gid)
                    LoadGroupMessages();
            }
        }

        private void ShowGroupMembers(int gid, string groupName)
        {
            using var dlg = new GroupMembersForm(gid, groupName);
            dlg.ShowDialog(this);

            if (dlg.Changed)
            {
                // Если меня самого исключили или состав изменился — перезагружаем сайдбар
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                if (_currentGroupId == gid)
                {
                    // Проверим, остался ли я в группе
                    if (!IsStillGroupMember(gid))
                        ClearChat();
                    else
                        LoadGroupMessages();
                }
            }
        }

        private bool IsStillGroupMember(int gid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@g", gid);
                cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch { return true; }
        }

        private void LeaveGroup(int gid, string groupName)
        {
            var confirm = MessageBox.Show(
                $"Покинуть группу «{groupName}»?",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@g", gid);
                cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();

                if (_currentGroupId == gid) ClearChat();

                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        private void btnNewGroup_Click(object sender, EventArgs e)
        {
            using var dlg = new CreateGroupForm();
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.CreatedGroupId > 0)
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                OpenGroup(dlg.CreatedGroupId, dlg.CreatedGroupName);
            }
        }


        private int CardWidth => Math.Max(200, pnlSidebar.Width - 12);

        private void AddUserCard(int uid, string name, string lastMsg, int unread)
        {
            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 62,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = uid
            };

            var avatar = new Panel { Size = new Size(38, 38), Location = new Point(10, 12) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, uid, 0, 0, avatar.Width - 1))
                {
                    e.Graphics.FillEllipse(new SolidBrush(GetAvatarColor(uid)),
                        0, 0, avatar.Width - 1, avatar.Height - 1);
                    string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 14f, FontStyle.Bold);
                    var sz = e.Graphics.MeasureString(letter, f);
                    e.Graphics.DrawString(letter, f, Brushes.White,
                        (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
                }
                DrawPresenceDot(e.Graphics, avatar.Width, avatar.Height, uid);
            };

            var lblName = new Label
            {
                Text = name,
                Font = unread > 0
                    ? new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
                    : new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 10),
                Size = new Size(pnl.Width - 90, 20),
                AutoEllipsis = true
            };

            var lblLast = new Label
            {
                Text = lastMsg.Length > 42 ? lastMsg[..42] + "…" : lastMsg,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 32),
                Size = new Size(pnl.Width - 90, 18),
                AutoEllipsis = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);
            pnl.Controls.Add(lblLast);

            if (unread > 0)
                pnl.Controls.Add(MakeBadge(unread, pnl.Width));

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                56, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentChatPartnerId == uid
                    ? Color.FromArgb(65, 68, 75) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);
            // ЛКМ открывает чат; ПКМ — только меню действий (Panel/Label иначе
            // вызывают Click и на правую кнопку → чат открывался вместе с меню).
            pnl.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenChat(uid, name); };

            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenChat(uid, name); };
            }

            // ВАЖНО: задействуем контекстное меню из partial (AttachConversationContextMenu)
            // чтобы пункты блок/разблок/очистка переписки заработали.
            AttachConversationContextMenu(pnl, uid, name);

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _userPanels.Add(pnl);
        }

        private void AddAdminUserCard(int uid, string name, string role)
        {
            bool isAdminCard = (uid == UserSession.UserId);
            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 58,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = uid
            };

            var avatar = new Panel { Size = new Size(36, 36), Location = new Point(10, 11) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, uid, 0, 0, avatar.Width - 1))
                {
                    var col = isAdminCard ? Color.FromArgb(88, 101, 242) : GetAvatarColor(uid);
                    e.Graphics.FillEllipse(new SolidBrush(col),
                        0, 0, avatar.Width - 1, avatar.Height - 1);
                    string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 12f, FontStyle.Bold);
                    var sz = e.Graphics.MeasureString(letter, f);
                    e.Graphics.DrawString(letter, f, Brushes.White,
                        (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
                }
                DrawPresenceDot(e.Graphics, avatar.Width, avatar.Height, uid);
            };

            var lblName = new Label
            {
                Text = isAdminCard ? $"{name} (Вы)" : name,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(54, 18),  // по центру по вертикали (нет подписи роли)
                Size = new Size(pnl.Width - 60, 22),
                AutoSize = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentChatPartnerId == uid
                    ? Color.FromArgb(65, 68, 75) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);

            void OpenDirect()
            {
                if (isAdminCard) return;
                UserSession.StopImpersonating();
                lblCurrentUser.Text = UserSession.UserName;
                RemoveExitImpersonateButton();
                OpenChat(uid, name);
            }

            pnl.Click += (s, e) => OpenDirect();
            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.Click += (s, e) => OpenDirect();
            }

            if (!isAdminCard)
            {
                var ctxMenu = new ContextMenuStrip();
                ctxMenu.Items.Add($"💬 Написать как Admin → {name}", null,
                    (s, ev) => OpenDirect());
                ctxMenu.Items.Add("👤 Профиль", null, (s, ev) =>
                {
                    using var pf = new ProfileForm(uid, readOnly: true);
                    pf.ShowDialog(this);
                });
                ctxMenu.Items.Add(new ToolStripSeparator());
                ctxMenu.Items.Add($"🚪 Войти за {name}", null,
                    (s, ev) => DoImpersonate(uid, name));

                // Дополнительно: пункты блокировки/очистки переписки в админской таблице
                ctxMenu.Items.Add(new ToolStripSeparator());

                bool alreadyBlocked = IsUserBlocked(UserSession.EffectiveId, uid);
                var itemBlock = new ToolStripMenuItem(
                    alreadyBlocked ? "✅ Разблокировать пользователя" : "🚫 Заблокировать пользователя");
                itemBlock.Click += (s, ev) =>
                {
                    try
                    {
                        if (IsUserBlocked(UserSession.EffectiveId, uid))
                        {
                            UnblockUser(UserSession.EffectiveId, uid);
                            MessageBox.Show($"Пользователь {name} разблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            BlockUser(UserSession.EffectiveId, uid);
                            MessageBox.Show($"Пользователь {name} заблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                ctxMenu.Items.Add(itemBlock);

                var itemClear = new ToolStripMenuItem("🗑 Очистить переписку");
                itemClear.Click += (s, ev) => DeleteConversationWithPartner(uid, name);
                ctxMenu.Items.Add(itemClear);

                pnl.ContextMenuStrip = ctxMenu;
                foreach (Control c in pnl.Controls)
                    c.ContextMenuStrip = ctxMenu;
            }

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                54, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _userPanels.Add(pnl);
        }

        private Label MakeBadge(int count, int parentWidth) => new Label
        {
            Text = count > 9 ? "9+" : count.ToString(),
            Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(240, 71, 71),
            Size = new Size(22, 18),
            Location = new Point(parentWidth - 34, 22),
            TextAlign = ContentAlignment.MiddleCenter
        };

        // ════════════════════════════════════════════════════════════════
        //  IMPERSONATION
        // ════════════════════════════════════════════════════════════════
        private Button _btnExitImpersonate;

        private void DoImpersonate(int uid, string name)
        {
            UserSession.ImpersonatedId = uid;
            UserSession.ImpersonatedName = name;
            lblCurrentUser.Text = $"👤 За: {name}";
            ShowExitImpersonateButton();
            ClearChat();
            LoadConversations();
        }

        private void ShowExitImpersonateButton()
        {
            RemoveExitImpersonateButton();

            _btnExitImpersonate = new Button
            {
                Text = "← Вернуться к себе",
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = Color.FromArgb(240, 71, 71),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnExitImpersonate.FlatAppearance.BorderSize = 0;
            _btnExitImpersonate.Click += (s, e) =>
            {
                UserSession.StopImpersonating();
                lblCurrentUser.Text = UserSession.UserName;
                RemoveExitImpersonateButton();
                ClearChat();
                LoadAllUsersForAdmin();
            };

            pnlSidebar.Controls.Add(_btnExitImpersonate);
            pnlSidebar.Controls.SetChildIndex(_btnExitImpersonate, 0);
        }

        private void RemoveExitImpersonateButton()
        {
            if (_btnExitImpersonate != null)
            {
                pnlSidebar.Controls.Remove(_btnExitImpersonate);
                _btnExitImpersonate.Dispose();
                _btnExitImpersonate = null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТКРЫТИЕ ЧАТА
        // ════════════════════════════════════════════════════════════════
        private void OpenChat(int partnerId, string partnerName)
        {
            _currentChatPartnerId = partnerId;
            _currentChatPartnerName = partnerName;
            _currentGroupId = -1;
            _currentGroupName = "";
            _lastMsgCount = 0;

            lblChatTitle.Text = "@ " + partnerName;

            foreach (var p in _userPanels)
                p.BackColor = (p.Tag is int id && id == partnerId)
                    ? Color.FromArgb(65, 68, 75) : Color.Transparent;
            foreach (var p in _groupPanels)
                p.BackColor = Color.Transparent;

            MarkAsRead(partnerId);
            LoadMessages();
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТКРЫТИЕ ГРУППОВОГО ЧАТА
        // ════════════════════════════════════════════════════════════════
        private void OpenGroup(int groupId, string groupName)
        {
            _currentGroupId = groupId;
            _currentGroupName = groupName;
            _currentChatPartnerId = -1;
            _currentChatPartnerName = "";
            _lastGroupMsgCount = 0;

            lblChatTitle.Text = "👥 " + groupName;

            foreach (var p in _groupPanels)
                p.BackColor = (p.Tag is int id && id == groupId)
                    ? Color.FromArgb(65, 68, 75) : Color.Transparent;
            foreach (var p in _userPanels)
                p.BackColor = Color.Transparent;

            LoadGroupMessages();
        }

        private void LoadGroupMessages()
        {
            if (_currentGroupId < 0) return;
            int group = _currentGroupId;
            int myId = UserSession.EffectiveId;

            // 1) Мгновенно рисуем из кеша (память → диск), чтобы открытие группы
            //    было без задержек, как в личных чатах.
            if (!_groupMetaCache.TryGetValue(group, out var cachedDt))
            {
                cachedDt = MessageCache.Load(MessageCache.GroupKey(group));
                if (cachedDt != null) _groupMetaCache[group] = cachedDt;
            }
            if (cachedDt != null) RenderGroupMessages(cachedDt, myId, group);

            // 2) Свежие данные тянем в ФОНЕ и перерисовываем, если всё ещё в группе.
            System.Threading.Tasks.Task.Run(() =>
            {
                DataTable dt = null;
                try { dt = LoadGroupMessagesMetaOnly(group); } catch { }
                if (dt == null) return;
                try { MessageCache.Save(MessageCache.GroupKey(group), dt); } catch { }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_currentGroupId != group) return; // уже переключились
                        _groupMetaCache[group] = dt;
                        RenderGroupMessages(dt, myId, group);
                    }));
                }
                catch { }
            });
        }

        /// <summary>Отрисовка групповой переписки из готового DataTable (из кеша
        /// мгновенно и из фоновой подгрузки).</summary>
        private void RenderGroupMessages(DataTable dt, int myId, int group)
        {
            if (_currentGroupId != group) return;

            // Пропускаем повторную отрисовку, если та же группа и данные не изменились.
            string key = "g" + group, sig = SigOf(dt);
            if (_renderedChatKey == key && _renderedChatSig == sig) return;
            _renderedChatKey = key; _renderedChatSig = sig;

            pnlMessages.SuspendLayout();
            DisposeAndClear(pnlMessages);

            try
            {
                int yOffset = 10;
                string lastDate = "";

                foreach (DataRow row in dt.Rows)
                {
                    int senderId = Convert.ToInt32(row["sender_id"]);
                    bool isMine = senderId == myId;
                    string text = Crypto.Dec(row["text"].ToString());
                    string sname = row["sender_name"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(sname)) sname = row["login"].ToString();
                    DateTime dt2 = Convert.ToDateTime(row["created_at"]);
                    string time = dt2.ToString("HH:mm");
                    string date = dt2.ToString("d MMMM yyyy",
                                        new System.Globalization.CultureInfo("ru-RU"));

                    if (date != lastDate)
                    {
                        var sep = BuildDateSeparator(date);
                        sep.Top = yOffset;
                        pnlMessages.Controls.Add(sep);
                        yOffset += sep.Height + 4;
                        lastDate = date;
                    }

                    int msgId = Convert.ToInt32(row["id"]);
                    int replyToId = row["reply_to_id"] == DBNull.Value ? 0 : Convert.ToInt32(row["reply_to_id"]);
                    bool isDeleted = Convert.ToBoolean(row["is_deleted"]);
                    bool isEdited = row["edited_at"] != DBNull.Value;
                    string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();

                    bool hasImg = row["has_img"] != DBNull.Value && Convert.ToBoolean(row["has_img"]);
                    bool hasAudio = row["has_audio"] != DBNull.Value && Convert.ToBoolean(row["has_audio"]);
                    bool hasVideo = row["has_video"] != DBNull.Value && Convert.ToBoolean(row["has_video"]);
                    bool hasFile = row["has_file"] != DBNull.Value && Convert.ToBoolean(row["has_file"]);

                    long fileSize = row.Table.Columns.Contains("file_size") && row["file_size"] != DBNull.Value
                        ? Convert.ToInt64(row["file_size"]) : -1;

                    var (img, audio, video, fileData) = LoadMediaForMessage(
                        msgId, fileName, hasImg, hasAudio, hasVideo, hasFile, isGroup: true, fileSize);

                    var bubble = BuildBubble(sname, time, text, img, audio, isMine, video,
                        fileData, fileName, msgId, isGroup: true, replyToId, isDeleted, isEdited, fileSize);
                    bubble.Top = yOffset;
                    PositionBubble(bubble, isMine);
                    bubble.Tag = isMine;

                    pnlMessages.Controls.Add(bubble);
                    yOffset += bubble.Height + 8;
                }

                _lastGroupMsgCount = dt.Rows.Count;
                pnlMessages.ResumeLayout();

                // ← ИСПРАВЛЕНИЕ: прокручиваем в конец ПОСЛЕ ResumeLayout
                pnlMessages.AutoScrollPosition = new Point(0, int.MaxValue);
            }
            catch (Exception ex)
            {
                pnlMessages.ResumeLayout();
                MessageBox.Show("Ошибка загрузки сообщений группы: " + ex.Message);
            }
        }

        private void SendGroupMessage(string text, byte[] imageData,
            byte[] audioData = null, byte[] videoData = null,
            byte[] fileData = null, string fileName = null)
        {
            int myId = UserSession.EffectiveId;

            if (fileData != null && fileData.LongLength > 0)
            {
                bool ok = SendFileWithProgress(isGroup: true, target: _currentGroupId, myId: myId, text: text,
                    imageData: imageData, audioData: audioData, videoData: videoData,
                    fileData: fileData, fileName: fileName);
                if (ok)
                    WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentGroupId, "group");
                else
                    return;
            }
            else
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "INSERT INTO group_messages " +
                    "(group_id, sender_id, text, image_data, audio_data, video_data, file_data, file_name) " +
                    "VALUES (@g, @s, @t, @img, @aud, @vid, @fd, @fn)";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@g", _currentGroupId);
                cmd.Parameters.AddWithValue("@s", myId);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));

                AddBlob(cmd, "@img", imageData);
                AddBlob(cmd, "@aud", audioData);
                AddBlob(cmd, "@vid", videoData);
                AddBlob(cmd, "@fd", fileData);
                cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);

                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentGroupId, "group");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки: " + ex.Message);
                return;
            }

            LoadGroupMessages();

            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();

            OpenGroup(_currentGroupId, _currentGroupName);
        }

        private void ClearChat()
        {
            _currentChatPartnerId = -1;
            _currentChatPartnerName = "";
            _currentGroupId = -1;
            _currentGroupName = "";
            _lastMsgCount = 0;
            _lastGroupMsgCount = 0;
            lblChatTitle.Text = "# Выберите диалог";
            DisposeAndClear(pnlMessages);
            _renderedChatKey = null; _renderedChatSig = null;
        }

        // ════════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА И РЕНДЕР СООБЩЕНИЙ
        // ════════════════════════════════════════════════════════════════
        private void LoadMessages()
        {
            if (_currentChatPartnerId < 0) return;
            int partner = _currentChatPartnerId;
            int myId = UserSession.EffectiveId;

            // 1) Мгновенно рисуем из кеша (если уже открывали этот чат) — чтобы
            //    переключение между чатами было без задержек, даже под VPN.
            //    Память → диск (постоянный кеш переписки) → пусто.
            if (!_msgMetaCache.TryGetValue(partner, out var cachedDt))
            {
                cachedDt = MessageCache.Load(MessageCache.DirectKey(myId, partner));
                if (cachedDt != null) _msgMetaCache[partner] = cachedDt;
            }
            if (cachedDt != null)
            {
                var (cib, ctb) = _blockCache.TryGetValue(partner, out var bc) ? bc : (false, false);
                RenderMessages(cachedDt, myId, partner, cib, ctb);
            }

            // 2) Свежие данные тянем в ФОНЕ (запросы к БД не на UI-потоке) и
            //    перерисовываем, только если пользователь всё ещё в этом чате.
            System.Threading.Tasks.Task.Run(() =>
            {
                bool iB = false, tB = false;
                DataTable dt = null;
                try
                {
                    iB = IsUserBlocked(myId, partner);
                    tB = IsUserBlocked(partner, myId);
                    dt = LoadMessagesMetaOnly(myId, partner);
                }
                catch { }
                if (dt == null) return;
                // Сохраняем в постоянный кеш переписки (текст зашифрован, как в БД).
                try { MessageCache.Save(MessageCache.DirectKey(myId, partner), dt); } catch { }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_currentChatPartnerId != partner) return; // уже переключились
                        _msgMetaCache[partner] = dt;
                        _blockCache[partner] = (iB, tB);
                        RenderMessages(dt, myId, partner, iB, tB);
                    }));
                }
                catch { }
            });
        }

        /// <summary>Отрисовка переписки из готового DataTable (без обращения к БД
        /// за метаданными). Вызывается из кеша мгновенно и из фоновой подгрузки.</summary>
        private void RenderMessages(DataTable dt, int myId, int partner, bool iBlocked, bool theyBlockedMe)
        {
            if (_currentChatPartnerId != partner) return;

            // Пропускаем повторную отрисовку, если тот же чат и данные не изменились.
            string key = "d" + partner, sig = SigOf(dt) + "|b" + (iBlocked ? 1 : 0) + (theyBlockedMe ? 1 : 0);
            if (_renderedChatKey == key && _renderedChatSig == sig) return;
            _renderedChatKey = key; _renderedChatSig = sig;

            pnlMessages.SuspendLayout();
            DisposeAndClear(pnlMessages);

            // Если кто-то заблокирован — показываем уведомление и блокируем отправку
            if (iBlocked || theyBlockedMe)
            {
                var notice = new Label
                {
                    Text = iBlocked
                        ? "Вы заблокировали этого пользователя — входящие сообщения скрыты."
                        : "Этот пользователь заблокировал вас — входящие сообщения скрыты.",
                    Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(185, 187, 190),
                    AutoSize = false,
                    Size = new Size(pnlMessages.ClientSize.Width - 40, 32),
                    Location = new Point(10, 10),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                pnlMessages.Controls.Add(notice);

                // Отключаем отправку в UI — чтобы пользователь видел, что чат в режиме блокировки
                try { btnSend.Enabled = false; txtMessage.Enabled = false; }
                catch { }
            }
            else
            {
                try { btnSend.Enabled = true; txtMessage.Enabled = true; }
                catch { }
            }

            try
            {
                int yOffset = (iBlocked || theyBlockedMe) ? 10 + 32 + 8 : 10;
                string lastDate = "";

                foreach (DataRow row in dt.Rows)
                {
                    int senderId = Convert.ToInt32(row["sender_id"]);
                    bool isMine = senderId == myId;

                    // Если чат в режиме блокировки — скрываем входящие сообщения партнёра
                    if ((iBlocked || theyBlockedMe) && !isMine)
                        continue;

                    string text = Crypto.Dec(row["text"].ToString());
                    string sname = row["sender_name"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(sname)) sname = row["login"].ToString();
                    DateTime dt2 = Convert.ToDateTime(row["created_at"]);
                    string time = dt2.ToString("HH:mm");
                    string date = dt2.ToString("d MMMM yyyy",
                                        new System.Globalization.CultureInfo("ru-RU"));

                    if (date != lastDate)
                    {
                        var sep = BuildDateSeparator(date);
                        sep.Top = yOffset;
                        pnlMessages.Controls.Add(sep);
                        yOffset += sep.Height + 4;
                        lastDate = date;
                    }

                    int msgId = Convert.ToInt32(row["id"]);
                    int replyToId = row["reply_to_id"] == DBNull.Value ? 0 : Convert.ToInt32(row["reply_to_id"]);
                    bool isDeleted = Convert.ToBoolean(row["is_deleted"]);
                    bool isEdited = row["edited_at"] != DBNull.Value;
                    string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();

                    bool hasImg = row["has_img"] != DBNull.Value && Convert.ToBoolean(row["has_img"]);
                    bool hasAudio = row["has_audio"] != DBNull.Value && Convert.ToBoolean(row["has_audio"]);
                    bool hasVideo = row["has_video"] != DBNull.Value && Convert.ToBoolean(row["has_video"]);
                    bool hasFile = row["has_file"] != DBNull.Value && Convert.ToBoolean(row["has_file"]);

                    long fileSize = row.Table.Columns.Contains("file_size") && row["file_size"] != DBNull.Value
                        ? Convert.ToInt64(row["file_size"]) : -1;

                    var (img, audio, video, fileData) = LoadMediaForMessage(
                        msgId, fileName, hasImg, hasAudio, hasVideo, hasFile, isGroup: false, fileSize);

                    // Статус прочтения для МОИХ сообщений: 0 — отправляется (1 серая),
                    // 1 — доставлено на сервер (2 серые), 2 — прочитано (2 синие).
                    int readState = -1;
                    if (isMine)
                    {
                        bool isRead = row.Table.Columns.Contains("is_read")
                            && row["is_read"] != DBNull.Value && Convert.ToInt32(row["is_read"]) != 0;
                        // Прочитано → ✓✓ синие; иначе доставлено → ✓✓ серые.
                        // (Состояние «отправляется по возрасту» убрано: оно зависело от
                        // age_sec, которого нет в подписи перерисовки → галочка застывала.)
                        readState = isRead ? 2 : 1;
                    }

                    var bubble = BuildBubble(sname, time, text, img, audio, isMine, video,
                        fileData, fileName, msgId, isGroup: false, replyToId, isDeleted, isEdited, fileSize, readState);
                    bubble.Top = yOffset;
                    PositionBubble(bubble, isMine);
                    bubble.Tag = isMine;

                    pnlMessages.Controls.Add(bubble);
                    yOffset += bubble.Height + 8;
                }

                _lastMsgCount = dt.Rows.Count;
                pnlMessages.ResumeLayout();

                // Прокручиваем в конец ПОСЛЕ ResumeLayout
                pnlMessages.AutoScrollPosition = new Point(0, int.MaxValue);
            }
            catch (Exception ex)
            {
                pnlMessages.ResumeLayout();
                MessageBox.Show("Ошибка загрузки сообщений: " + ex.Message);
            }
        }

        private Panel BuildDateSeparator(string dateText)
        {
            int w = pnlMessages.ClientSize.Width - 20;
            var p = new Panel { Width = w, Height = 28, BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var penLine = new Pen(Color.FromArgb(60, 255, 255, 255));
                using var fnt = new Font("Segoe UI", 8f);
                var sz = g.MeasureString(dateText, fnt);
                int cx = (p.Width - (int)sz.Width - 12) / 2;
                int cy = p.Height / 2;
                g.DrawLine(penLine, 0, cy, cx - 6, cy);
                g.DrawLine(penLine, cx + (int)sz.Width + 18, cy, p.Width, cy);
                g.DrawString(dateText, fnt, new SolidBrush(Color.FromArgb(114, 118, 125)),
                    cx + 6, cy - sz.Height / 2);
            };
            return p;
        }

        // Строит «пузырёк» сообщения
        private Panel BuildBubble(string senderName, string time, string text,
                                   byte[] imgBytes, byte[] audioBytes, bool isMine,
                                   byte[] videoBytes = null,
                                   byte[] fileData = null, string fileName = null,
                                   int msgId = -1, bool isGroup = false,
                                   int replyToId = 0, bool isDeleted = false,
                                   bool isEdited = false, long fileSize = -1,
                                   int readState = -1)
        {
            const int MAX_W = 480;
            const int PAD = 12;

            var bubble = new Panel
            {
                BackColor = isMine ? Color.FromArgb(88, 101, 242) : Color.FromArgb(64, 68, 75),
                MaximumSize = new Size(MAX_W, 0),
                MinimumSize = new Size(80, 36),
                AutoSize = false,
                Padding = new Padding(PAD),
                Cursor = Cursors.Default
            };

            bubble.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new SolidBrush(bubble.BackColor);
                e.Graphics.FillRoundedRectangle(br, 0, 0, bubble.Width - 1, bubble.Height - 1, 10);
            };
            bubble.Region = System.Drawing.Region.FromHrgn(
                NativeMethods.CreateRoundRectRgn(0, 0, MAX_W, 999, 10, 10));

            int innerY = PAD;
            int innerW = MAX_W - PAD * 2 - 4;

            // Имя отправителя (для чужих сообщений)
            if (!isMine)
            {
                var lblHeader = new Label
                {
                    Text = senderName,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    ForeColor = GetAvatarColor(0),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblHeader);
                innerY += lblHeader.PreferredHeight + 2;
            }

            // Цитата ответа (если сообщение является ответом)
            if (!isDeleted && replyToId > 0 && msgId > 0)
            {
                int quoteH = BuildReplyQuote(bubble, replyToId, isGroup, isMine, innerY, innerW, PAD);
                if (quoteH > 0) innerY += quoteH + 4;
            }

            // Удалённое сообщение — показываем плейсхолдер и завершаем рендер
            if (isDeleted)
            {
                var lblDeleted = new Label
                {
                    Text = "🚫 Сообщение удалено",
                    Font = new Font("Segoe UI Italic", 9.5f, FontStyle.Italic),
                    ForeColor = Color.FromArgb(150, isMine ? 255 : 220, isMine ? 255 : 221),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblDeleted);
                innerY += lblDeleted.PreferredHeight + 4;

                var lblTimeDel = new Label
                {
                    Text = time,
                    Font = new Font("Segoe UI", 7.5f),
                    ForeColor = Color.FromArgb(isMine ? 180 : 114, isMine ? 186 : 118, isMine ? 255 : 125),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblTimeDel);
                innerY += lblTimeDel.PreferredHeight + PAD;

                bubble.Size = new Size(Math.Max(160, CalcBubbleWidth(bubble, PAD)), innerY);
                bubble.Region = System.Drawing.Region.FromHrgn(
                    NativeMethods.CreateRoundRectRgn(0, 0, bubble.Width, bubble.Height, 10, 10));
                return bubble;
            }

            // Видео-кружочек (приоритет над картинкой/аудио — самодостаточный элемент)
            if (videoBytes is { Length: > 0 })
            {
                try
                {
                    const int circleD = 180;
                    var player = new VideoCirclePlayer(videoBytes, circleD)
                    {
                        Location = new Point(PAD, innerY)
                    };
                    bubble.Controls.Add(player);
                    innerY += circleD + 6;
                }
                catch
                {
                    bubble.Controls.Add(ErrLabel("⚠ Не удалось загрузить видео-кружочек", PAD, innerY));
                    innerY += 20 + 6;
                }
            }

            // GIF или изображение
            if (imgBytes is { Length: > 0 })
            {
                try
                {
                    // Копируем байты чтобы ms жил независимо
                    var ms = new MemoryStream(imgBytes.ToArray());
                    Image img;
                    try { img = Image.FromStream(ms); }
                    catch { ms.Dispose(); throw; }

                    int maxW = 260, maxH = 200;
                    double ratio = Math.Min((double)maxW / img.Width, (double)maxH / img.Height);
                    int dw = Math.Max(1, (int)(img.Width * ratio));
                    int dh = Math.Max(1, (int)(img.Height * ratio));

                    var pb = new PictureBox
                    {
                        SizeMode = PictureBoxSizeMode.StretchImage,
                        Size = new Size(dw, dh),
                        Location = new Point(PAD, innerY),
                        Cursor = Cursors.Hand
                    };

                    // GIF: анимация через Timer.
                    // КРИТИЧНО: PictureBox автоматически анимирует МНОГОКАДРОВЫЙ
                    // Bitmap через ImageAnimator (а он падает в GDI+ "A generic error
                    // occurred"). Поэтому НИКОГДА не присваиваем pb.Image сам GIF или
                    // его Clone() — заранее рендерим каждый кадр в ОТДЕЛЬНЫЙ одно-
                    // кадровый Bitmap, а таймер просто переключает их.
                    if (IsGif(imgBytes) && img is Bitmap gifBmp)
                    {
                        var dimension = new System.Drawing.Imaging.FrameDimension(
                            gifBmp.FrameDimensionsList[0]);
                        int frameCount = Math.Max(1, gifBmp.GetFrameCount(dimension));

                        // Задержки кадров из метаданных GIF (PropertyTagFrameDelay = 0x5100)
                        int[] delays;
                        try
                        {
                            var prop = gifBmp.GetPropertyItem(0x5100);
                            delays = new int[frameCount];
                            for (int i = 0; i < frameCount; i++)
                                delays[i] = Math.Max(50,
                                    BitConverter.ToInt32(prop.Value, i * 4) * 10); // в мс
                        }
                        catch { delays = new int[frameCount]; Array.Fill(delays, 100); }

                        // Заранее извлекаем все кадры как самостоятельные одно-кадровые битмапы.
                        var frames = new Bitmap[frameCount];
                        try
                        {
                            for (int i = 0; i < frameCount; i++)
                            {
                                gifBmp.SelectActiveFrame(dimension, i);
                                var f = new Bitmap(gifBmp.Width, gifBmp.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                using (var g = Graphics.FromImage(f))
                                    g.DrawImage(gifBmp, 0, 0, gifBmp.Width, gifBmp.Height);
                                frames[i] = f;
                            }
                        }
                        catch { /* что извлекли — то и покажем */ }

                        // Исходный многокадровый GIF больше не нужен.
                        img.Dispose();
                        ms.Dispose();

                        int frameIdx = 0;
                        pb.Image = frames[0];

                        if (frameCount > 1)
                        {
                            var gifTimer = new System.Windows.Forms.Timer { Interval = delays[0] };
                            bool disposed = false;
                            gifTimer.Tick += (s, e) =>
                            {
                                if (disposed || pb.IsDisposed) { gifTimer.Stop(); return; }
                                frameIdx = (frameIdx + 1) % frameCount;
                                var nf = frames[frameIdx];
                                if (nf == null) return;
                                pb.Image = nf; // одно-кадровый битмап — ImageAnimator не вызывается
                                gifTimer.Interval = delays[frameIdx];
                            };
                            gifTimer.Start();

                            pb.Disposed += (s, e) =>
                            {
                                disposed = true;
                                gifTimer.Stop();
                                gifTimer.Dispose();
                                foreach (var f in frames) { try { f?.Dispose(); } catch { } }
                            };
                        }
                        else
                        {
                            pb.Disposed += (s, e) =>
                            {
                                foreach (var f in frames) { try { f?.Dispose(); } catch { } }
                            };
                        }
                    }
                    else
                    {
                        pb.Image = img;
                        pb.Disposed += (s, e) => { img.Dispose(); ms.Dispose(); };
                    }

                    var cap = imgBytes;
                    pb.Click += (s, e) => ShowImageFullscreen(cap);
                    bubble.Controls.Add(pb);
                    innerY += dh + 6;
                }
                catch
                {
                    bubble.Controls.Add(ErrLabel("⚠ Не удалось загрузить изображение", PAD, innerY));
                    innerY += 20 + 6;
                }
            }

            // Голосовое сообщение
            if (audioBytes is { Length: > 0 })
            {
                var btnPlay = new Button
                {
                    Text = "▶  Голосовое",
                    FlatStyle = FlatStyle.Flat,
                    BackColor = isMine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(47, 49, 54),
                    ForeColor = Color.FromArgb(220, 221, 222),
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Size = new Size(170, 36),
                    Location = new Point(PAD, innerY),
                    Cursor = Cursors.Hand
                };
                btnPlay.FlatAppearance.BorderSize = 0;

                var capturedAudio = audioBytes;
                btnPlay.Click += (s, e) => PlayAudio(capturedAudio, btnPlay);

                bubble.Controls.Add(btnPlay);
                innerY += btnPlay.Height + 6;
            }

            // Видео-файл со встроенным проигрывателем (как в Telegram): если байты
            // уже загружены — показываем видео прямо в пузыре; иначе обычная карточка.
            bool inlineVideoShown = false;
            if (!string.IsNullOrWhiteSpace(fileName)
                && MediaPlayerForm.IsVideo(Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant())
                && fileData is { Length: > 0 })
            {
                try
                {
                    int boxW = Math.Min(innerW, 280);
                    int boxH = (int)(boxW * 1.2); // вертикальный бокс с леттербоксом
                    var vp = new InlineVideoPlayer(fileData, fileName, boxW, boxH)
                    {
                        Location = new Point(PAD, innerY)
                    };
                    bubble.Controls.Add(vp);
                    innerY += boxH + 6;
                    inlineVideoShown = true;
                }
                catch { inlineVideoShown = false; }
            }

            // Документ / архив (теперь проверяем только fileName, так как fileData загружается по требованию)
            if (!inlineVideoShown && !string.IsNullOrWhiteSpace(fileName))
            {
                var pnlFile = BuildFileCard(fileData, fileName, isMine, innerW, msgId, isGroup, fileSize);
                pnlFile.Location = new Point(PAD, innerY);
                bubble.Controls.Add(pnlFile);
                innerY += pnlFile.Height + 6;
            }

            // Текст (выделяемый — можно выделить часть и скопировать через Ctrl+C
            // или правый клик, а не только всё сообщение целиком).
            if (!string.IsNullOrEmpty(text))
            {
                var txtMsg = MakeSelectableText(text, bubble.BackColor,
                    Color.FromArgb(235, 236, 240), new Font("Segoe UI", 10.5f), innerW);
                txtMsg.Location = new Point(PAD, innerY);
                bubble.Controls.Add(txtMsg);
                innerY += txtMsg.Height + 4;
            }

            // Время (+ "изменено")
            var lblTime = new Label
            {
                Text = isEdited ? $"{time} · изменено" : time,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(isMine ? 180 : 114, isMine ? 186 : 118, isMine ? 255 : 125),
                AutoSize = true,
                Location = new Point(PAD, innerY)
            };
            bubble.Controls.Add(lblTime);

            // Галочки прочтения (только для моих личных сообщений):
            // 0 — ✓ серая (отправляется), 1 — ✓✓ серые (доставлено), 2 — ✓✓ синие (прочитано).
            if (readState >= 0)
            {
                var lblCheck = new Label
                {
                    Text = readState == 0 ? "✓" : "✓✓",
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    ForeColor = readState == 2
                        ? Color.FromArgb(88, 170, 255)            // прочитано — синие
                        : Color.FromArgb(150, 160, 180),          // доставлено/отправка — серые
                    AutoSize = true,
                    Location = new Point(lblTime.Right + 4, innerY)
                };
                bubble.Controls.Add(lblCheck);
            }

            innerY += lblTime.PreferredHeight + PAD;

            bubble.Size = new Size(
                Math.Max(120, CalcBubbleWidth(bubble, PAD)),
                innerY);

            bubble.Region = System.Drawing.Region.FromHrgn(
                NativeMethods.CreateRoundRectRgn(0, 0, bubble.Width, bubble.Height, 10, 10));

            // Контекстное меню (ответ/пересылка/копировать/редактировать/удалить)
            if (msgId > 0)
                AttachBubbleContextMenu(bubble, msgId, isGroup, isMine, text, senderName);

            return bubble;
        }

        /// <summary>Текст сообщения, который можно выделять и копировать (read-only
        /// TextBox без рамки, выглядит как подпись). Высота считается по содержимому.</summary>
        internal static TextBox MakeSelectableText(string text, Color back, Color fore, Font font, int maxW)
        {
            var tb = new TextBox
            {
                Text = text,
                ReadOnly = true,
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = back,
                ForeColor = fore,
                Font = font,
                TabStop = false,
                Cursor = Cursors.IBeam,
                ScrollBars = ScrollBars.None
            };
            int w = Math.Min(TextRenderer.MeasureText(text, font).Width + 6, maxW);
            int h = TextRenderer.MeasureText(text, font, new Size(w, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 4;
            tb.Size = new Size(w, h);
            return tb;
        }

        private int CalcBubbleWidth(Panel bubble, int pad)
        {
            int max = 80;
            foreach (Control c in bubble.Controls)
                max = Math.Max(max, c.Right + pad);
            return Math.Min(max, 480);
        }

        private void PositionBubble(Panel bubble, bool isMine)
        {
            int areaW = pnlMessages.ClientSize.Width - 20;
            bubble.Left = isMine
                ? Math.Max(8, areaW - bubble.Width - 8)
                : 8;
        }

        private void RefreshBubblePositions()
        {
            foreach (Control c in pnlMessages.Controls)
                if (c is Panel bubble && bubble.Tag is bool isMine)
                    PositionBubble(bubble, isMine);
        }

        // ════════════════════════════════════════════════════════════════
        //  ГОЛОСОВЫЕ СООБЩЕНИЯ
        // ════════════════════════════════════════════════════════════════
        private void btnVoice_MouseDown(object sender, MouseEventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            try
            {
                _audioStream = new MemoryStream();
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 1)
                };

                if (DeviceSettings.MicrophoneIndex >= 0 &&
                    DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                {
                    _waveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;
                }

                _waveWriter = new WaveFileWriter(_audioStream, _waveIn.WaveFormat);

                float gain = DeviceSettings.MicrophoneGain;
                _waveIn.DataAvailable += (s, ev) =>
                {
                    if (Math.Abs(gain - 1f) > 0.01f)
                    {
                        var buf = ApplyGain(ev.Buffer, ev.BytesRecorded, gain);
                        _waveWriter?.Write(buf, 0, ev.BytesRecorded);
                    }
                    else
                    {
                        _waveWriter?.Write(ev.Buffer, 0, ev.BytesRecorded);
                    }
                };

                _waveIn.StartRecording();
                btnVoice.ForeColor = Color.FromArgb(240, 71, 71);
                btnVoice.Text = "🔴";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Нет доступа к микрофону: " + ex.Message);
            }
        }

        private void btnVoice_MouseUp(object sender, MouseEventArgs e)
        {
            if (_waveIn == null) return;

            try
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;

                _waveWriter.Flush();
                byte[] audioBytes = _audioStream.ToArray();

                _waveWriter.Dispose();
                _waveWriter = null;
                _audioStream.Dispose();
                _audioStream = null;

                btnVoice.ForeColor = Color.FromArgb(142, 146, 151);
                btnVoice.Text = "🎤";

                if (audioBytes.Length > 4000)  // ~0.1 сек минимум
                {
                    if (_currentGroupId >= 0) SendGroupMessage("", null, audioBytes);
                    else SendMessage("", null, audioBytes);
                }
            }
            catch { }
        }

        /// <summary>Применяет множитель усиления к 16-битному PCM моно буферу.</summary>
        private static byte[] ApplyGain(byte[] buffer, int bytesRecorded, float gain)
        {
            var result = new byte[bytesRecorded];
            Array.Copy(buffer, result, bytesRecorded);

            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                short sample = (short)((result[i + 1] << 8) | result[i]);
                int amplified = (int)(sample * gain);
                amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
                result[i] = (byte)(amplified & 0xFF);
                result[i + 1] = (byte)((amplified >> 8) & 0xFF);
            }

            return result;
        }

        // Статический проигрыватель голосовых (для переиспользования в ServersForm).
        private static WaveOutEvent _voiceOutStatic;
        internal static void PlayVoiceClip(byte[] audioBytes, Button btn)
        {
            if (_voiceOutStatic != null)
            {
                try { _voiceOutStatic.Stop(); _voiceOutStatic.Dispose(); } catch { }
                _voiceOutStatic = null;
                btn.Text = "▶  Голосовое";
                return;
            }
            try
            {
                var ms = new MemoryStream(audioBytes);
                var reader = new WaveFileReader(ms);
                _voiceOutStatic = new WaveOutEvent();
                _voiceOutStatic.Init(reader);
                _voiceOutStatic.Play();
                btn.Text = "⏹  Остановить";
                _voiceOutStatic.PlaybackStopped += (s, ev) =>
                {
                    try { btn.BeginInvoke(new Action(() => {
                        btn.Text = "▶  Голосовое";
                        try { _voiceOutStatic?.Dispose(); } catch { }
                        _voiceOutStatic = null;
                        try { reader.Dispose(); ms.Dispose(); } catch { }
                    })); } catch { }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка воспроизведения: " + ex.Message);
            }
        }

        private void PlayAudio(byte[] audioBytes, Button btn)
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
                btn.Text = "▶  Голосовое";
                return;
            }

            try
            {
                var ms = new MemoryStream(audioBytes);
                var reader = new WaveFileReader(ms);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(reader);
                _waveOut.Play();

                btn.Text = "⏹  Остановить";

                _waveOut.PlaybackStopped += (s, ev) =>
                {
                    try
                    {
                        this.Invoke(() =>
                        {
                            btn.Text = "▶  Голосовое";
                            _waveOut?.Dispose();
                            _waveOut = null;
                        });
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка воспроизведения: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТПРАВКА
        // ════════════════════════════════════════════════════════════════
        private void btnSend_Click(object sender, EventArgs e)
        {
            bool isGroup = _currentGroupId >= 0;

            if (_currentChatPartnerId < 0 && !isGroup)
            {
                MessageBox.Show("Выберите собеседника в левой панели.");
                return;
            }

            // Режим редактирования — сохраняем и выходим
            if (TrySaveEdit()) return;

            string text = txtMessage.Text.Trim();

            // Режим пересылки — пересылаем введённый текст (или исходный текст сообщения)
            if (_forwardMsgId >= 0)
            {
                if (TrySendForward()) return;
            }

            // Прикреплённый файл/изображение/GIF
            if (_pendingAttach != null)
            {
                if (_pendingAttach.Kind == AttachKind.Image || _pendingAttach.Kind == AttachKind.Gif)
                {
                    if (isGroup) SendGroupMessage(text, _pendingAttach.Data, null, null, null, null);
                    else SendMessage(text, _pendingAttach.Data, null, null, null, null);
                }
                else
                {
                    if (isGroup) SendGroupMessage(text, null, null, null, _pendingAttach.Data, _pendingAttach.FileName);
                    else SendMessage(text, null, null, null, _pendingAttach.Data, _pendingAttach.FileName);
                }

                _pendingAttach = null;
                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
                txtMessage.Clear();
                ApplyReplyToLastMessage(isGroup);
                return;
            }

            // Совместимость: старый _pendingImageBytes (Ctrl+V без _pendingAttach)
            if (_pendingImageBytes != null)
            {
                if (isGroup) SendGroupMessage(text, _pendingImageBytes, null, null, null, null);
                else SendMessage(text, _pendingImageBytes, null, null, null, null);

                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
                txtMessage.Clear();
                ApplyReplyToLastMessage(isGroup);
                return;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            if (isGroup) SendGroupMessage(text, null, null, null, null, null);
            else SendMessage(text, null, null, null, null, null);

            txtMessage.Clear();
            ApplyReplyToLastMessage(isGroup);
        }

        private void SendMessage(string text, byte[] imageData,
    byte[] audioData = null, byte[] videoData = null,
    byte[] fileData = null, string fileName = null)
        {
            int myId = UserSession.EffectiveId;
            int themId = _currentChatPartnerId;

            // Блокировки: если я заблокировал собеседника или он заблокировал меня — не шлём
            try
            {
                if (IsUserBlocked(myId, themId))
                {
                    MessageBox.Show("Нельзя отправлять — вы заблокировали этого пользователя.", "PISMO",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (IsUserBlocked(themId, myId))
                {
                    MessageBox.Show("Нельзя отправлять — этот пользователь заблокировал вас.", "PISMO",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { /* если проверка по какой-то причине упала — допускаем отправку */ }

            // Файл крупного размера отправляем чанками с круговым прогрессом.
            if (fileData != null && fileData.LongLength > 0)
            {
                bool ok = SendFileWithProgress(isGroup: false, target: themId, myId: myId, text: text,
                    imageData: imageData, audioData: audioData, videoData: videoData,
                    fileData: fileData, fileName: fileName);
                if (ok)
                    WebSocketSignalingClient.Instance.SendMessage("new_message", 0, themId, "direct");
                else
                    return;
            }
            else
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "INSERT INTO messages " +
                    "(sender_id, receiver_id, text, image_data, audio_data, video_data, file_data, file_name) " +
                    "VALUES (@s, @r, @t, @img, @aud, @vid, @fd, @fn)";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@s", myId);
                cmd.Parameters.AddWithValue("@r", themId);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));

                AddBlob(cmd, "@img", imageData);
                AddBlob(cmd, "@aud", audioData);
                AddBlob(cmd, "@vid", videoData);
                AddBlob(cmd, "@fd", fileData);
                cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);

                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, themId, "direct");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки: " + ex.Message);
                return;
            }

            LoadMessages();
            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();

            OpenChat(_currentChatPartnerId, _currentChatPartnerName);
        }

        /// <summary>
        /// Загружает файловое сообщение на сервер чанками с круговым индикатором
        /// прогресса. Сначала вставляет строку с метаданными (file_data=NULL),
        /// затем дописывает file_data порциями (виден заполняющийся кружок).
        /// Возвращает true при успехе. При любой ошибке/больших файлах вызывающий
        /// код может откатиться на обычную единоразовую вставку.
        /// </summary>
        private bool SendFileWithProgress(bool isGroup, int target, int myId, string text,
            byte[] imageData, byte[] audioData, byte[] videoData, byte[] fileData, string fileName)
        {
            long total = fileData.LongLength;
            string table = isGroup ? "group_messages" : "messages";

            var dlg = new Form
            {
                Text = "Отправка файла",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                ClientSize = new Size(300, 150),
                BackColor = Color.FromArgb(40, 42, 46),
                ControlBox = false
            };
            double prog = 0;
            var pic = new Panel { Size = new Size(72, 72), Location = new Point(114, 14), BackColor = Color.Transparent };
            pic.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(6, 6, 58, 58);
                using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 6);
                using var arc = new Pen(Color.FromArgb(88, 101, 242), 6);
                e.Graphics.DrawEllipse(track, rect);
                e.Graphics.DrawArc(arc, rect, -90, (float)(360 * Math.Min(1.0, prog)));
                using var f = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString($"{(int)(prog * 100)}%", f, Brushes.White, rect, sf);
            };
            var lbl = new Label
            {
                Text = $"Отправка {fileName}\n({FormatFileSize(total)})",
                ForeColor = Color.FromArgb(220, 221, 222),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 92), Size = new Size(280, 46),
                Font = new Font("Segoe UI", 9f)
            };
            dlg.Controls.Add(pic);
            dlg.Controls.Add(lbl);

            // Заливка идёт одним запросом (нельзя дёшево отслеживать байты), поэтому
            // крутим индикатор плавно к 90% во время отправки, 100% — по завершении.
            var animTimer = new System.Windows.Forms.Timer { Interval = 80 };
            animTimer.Tick += (s, e) => { if (prog < 0.9) { prog += (0.9 - prog) * 0.06 + 0.005; pic.Invalidate(); } };
            dlg.Shown += (s, e) => animTimer.Start();
            dlg.FormClosed += (s, e) => { try { animTimer.Stop(); animTimer.Dispose(); } catch { } };

            bool success = false;
            string err = null;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = DBHelper.OpenConnection();

                    // Файл пишем ОДНИМ запросом — без квадратичного CONCAT по порциям
                    // (он перечитывал/переписывал весь blob на каждой порции → для 60 МБ
                    // это ~гигабайты трафика к БД и жуткая медлительность).
                    string insSql = isGroup
                        ? "INSERT INTO group_messages (group_id, sender_id, text, image_data, audio_data, video_data, file_data, file_name) VALUES (@g,@s,@t,@img,@aud,@vid,@fd,@fn)"
                        : "INSERT INTO messages (sender_id, receiver_id, text, image_data, audio_data, video_data, file_data, file_name) VALUES (@s,@r,@t,@img,@aud,@vid,@fd,@fn)";
                    using (var ins = new MySqlCommand(insSql, conn))
                    {
                        if (isGroup) { ins.Parameters.AddWithValue("@g", target); ins.Parameters.AddWithValue("@s", myId); }
                        else { ins.Parameters.AddWithValue("@s", myId); ins.Parameters.AddWithValue("@r", target); }
                        ins.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));
                        AddBlob(ins, "@img", imageData);
                        AddBlob(ins, "@aud", audioData);
                        AddBlob(ins, "@vid", videoData);
                        AddBlob(ins, "@fd", fileData);
                        ins.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);
                        ins.ExecuteNonQuery();
                    }

                    try { dlg.BeginInvoke(() => { prog = 1.0; pic.Invalidate(); }); } catch { }
                    success = true;
                }
                catch (Exception ex) { err = ex.Message; }

                try { dlg.BeginInvoke(() => { dlg.Close(); }); } catch { }
            });

            dlg.ShowDialog(this);
            if (!success && err != null)
                MessageBox.Show("Ошибка отправки файла: " + err, "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return success;
        }

        /// <summary>Добавляет BLOB-параметр (LongBlob) или DBNull, если данных нет.</summary>
        private static void AddBlob(MySqlCommand cmd, string name, byte[] data)
        {
            if (data != null)
                cmd.Parameters.Add(name, MySqlDbType.LongBlob).Value = data;
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        // ════════════════════════════════════════════════════════════════
        //  ВЛОЖЕНИЯ (ИЗОБРАЖЕНИЯ)
        // ════════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════════
        //  ВИДЕО-КРУЖОЧКИ
        // ════════════════════════════════════════════════════════════════
        private void btnVideoCircle_Click(object sender, EventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            using var dlg = new VideoCircleRecordForm();
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ResultVideoData != null)
            {
                if (_currentGroupId >= 0)
                    SendGroupMessage("", null, null, dlg.ResultVideoData);
                else
                    SendMessage("", null, null, dlg.ResultVideoData);
            }
        }

        private void btnAttach_Click(object sender, EventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            using var dlg = new OpenFileDialog
            {
                Title = "Выбрать файл для отправки",
                Filter =
                    "Изображения и GIF|*.jpg;*.jpeg;*.png;*.bmp;*.gif|" +
                    "Видео и музыка|*.mp4;*.webm;*.m4v;*.mov;*.mp3;*.wav;*.ogg;*.m4a;*.aac;*.flac;*.opus|" +
                    "Документы|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.pdf;*.txt;*.rtf|" +
                    "Архивы|*.zip;*.rar;*.7z;*.tar;*.gz|" +
                    "Все файлы|*.*",
                FilterIndex = 1
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            byte[] bytes;
            try { bytes = File.ReadAllBytes(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось прочитать файл:\n" + ex.Message);
                return;
            }

            const long MAX = 64L * 1024 * 1024; // 64 МБ
            if (bytes.Length > MAX)
            {
                MessageBox.Show($"Файл слишком большой ({bytes.Length / 1024 / 1024} МБ).\nМаксимум — 64 МБ.");
                return;
            }

            bool isGif = ext == ".gif";
            bool isImg = !isGif && new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext);

            AttachKind kind;
            if (isGif) kind = AttachKind.Gif;
            else if (isImg) kind = AttachKind.Image;
            else kind = AttachKind.File;

            if (isImg && bytes.Length > 2 * 1024 * 1024)
                bytes = CompressImageIfNeeded(bytes);

            _pendingAttach = new PendingAttachment(bytes, Path.GetFileName(dlg.FileName), kind);
            ShowPreview(_pendingAttach);
        }

        /// <summary>Показывает превью прикреплённого файла/изображения/GIF над полем ввода.</summary>
        private void ShowPreview(PendingAttachment att)
        {
            pnlPreview.Controls.Clear();

            Control icon;
            if (att.Kind == AttachKind.Image || att.Kind == AttachKind.Gif)
            {
                try
                {
                    var pb = new PictureBox
                    {
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(54, 54),
                        Location = new Point(8, 7)
                    };
                    using var ms = new MemoryStream(att.Data);
                    pb.Image = Image.FromStream(ms);
                    icon = pb;
                }
                catch { icon = MakeFileIcon(att.FileName); }
            }
            else
            {
                icon = MakeFileIcon(att.FileName);
            }
            pnlPreview.Controls.Add(icon);

            long kb = att.Data.Length / 1024;
            string sz = kb > 1024 ? $"{kb / 1024.0:F1} МБ" : $"{kb} КБ";
            var lbl = new Label
            {
                Text = $"{att.FileName}  ({sz})",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                Location = new Point(70, 20),
                AutoSize = true
            };
            pnlPreview.Controls.Add(lbl);

            var btnCancel = new Button
            {
                Text = "✕",
                Size = new Size(28, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(185, 187, 190),
                Font = new Font("Segoe UI", 10f),
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Location = new Point(pnlPreview.Width - 36, 18);
            btnCancel.Click += (s, ev) =>
            {
                _pendingAttach = null;
                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
            };
            pnlPreview.Resize += (s, ev) =>
                btnCancel.Location = new Point(pnlPreview.Width - 36, 18);

            pnlPreview.Controls.Add(btnCancel);
            pnlPreview.Visible = true;
        }

        /// <summary>Универсальная иконка-плейсхолдер для файлов, не являющихся изображением.</summary>
        private static Panel MakeFileIcon(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');
            Color bg = ext switch
            {
                "pdf" => Color.FromArgb(220, 53, 53),
                "doc" or "docx" => Color.FromArgb(41, 86, 163),
                "xls" or "xlsx" => Color.FromArgb(32, 120, 62),
                "ppt" or "pptx" => Color.FromArgb(198, 69, 30),
                "zip" or "rar" or "7z" or "tar" or "gz" => Color.FromArgb(140, 90, 20),
                "txt" or "rtf" => Color.FromArgb(80, 80, 80),
                _ => Color.FromArgb(88, 101, 242),
            };

            var p = new Panel { Size = new Size(54, 54), Location = new Point(8, 7), BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillRoundedRectangle(new SolidBrush(bg), 0, 0, 53, 53, 8);
                string label = ext.Length > 0 ? ext.ToUpper() : "FILE";
                using var f = new Font("Segoe UI Black", label.Length > 3 ? 7.5f : 9.5f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(label, f, Brushes.White, new RectangleF(0, 0, 54, 54), sf);
            };
            return p;
        }

        /// <summary>Сжимает изображение в JPEG (качество 78), если оно больше 2 МБ.</summary>
        private static byte[] CompressImageIfNeeded(byte[] data)
        {
            const int threshold = 2 * 1024 * 1024;
            if (data == null || data.Length <= threshold) return data;
            try
            {
                using var ms = new MemoryStream(data);
                using var img = Image.FromStream(ms);
                ImageCodecInfo jpegCodec = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.FormatID == ImageFormat.Jpeg.Guid) { jpegCodec = c; break; }
                if (jpegCodec == null) return data;

                var enc = new EncoderParameters(1);
                enc.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
                using var outMs = new MemoryStream();
                img.Save(outMs, jpegCodec, enc);
                var compressed = outMs.ToArray();
                return compressed.Length < data.Length ? compressed : data;
            }
            catch { return data; }
        }

        /// <summary>Проверяет magic bytes GIF-файла.</summary>
        private static bool IsGif(byte[] data)
            => data != null && data.Length >= 3
               && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46; // "GIF"

        private static Label ErrLabel(string msg, int x, int y) => new Label
        {
            Text = msg,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(240, 71, 71),
            AutoSize = true,
            Location = new Point(x, y)
        };

        /// <summary>Человекочитаемый размер файла.</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "";
            double kb = bytes / 1024.0;
            return kb > 1024 ? $"{kb / 1024.0:F1} МБ" : $"{Math.Max(1, (long)kb)} КБ";
        }

        /// <summary>Карточка документа/архива внутри пузырька — клик загружает с сервера
        /// (с круговым индикатором прогресса), сохраняет и открывает файл.
        /// knownSize — размер файла в байтах (показывается ДО загрузки).</summary>
        internal static Panel BuildFileCard(byte[] fileData, string fileName, bool isMine, int maxW, int msgId, bool isGroup, long knownSize = -1)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');
            bool isMedia = MediaPlayerForm.IsMedia(ext);
            bool isVideoMedia = MediaPlayerForm.IsVideo(ext);
            long displaySize = fileData != null ? fileData.Length : knownSize;
            // Размер показываем сразу (если известен), даже до загрузки самого файла.
            string actionHint = isMedia ? "нажмите для воспроизведения" : "нажмите для загрузки";
            string szStr = fileData != null
                ? FormatFileSize(fileData.Length)
                : (displaySize > 0 ? $"{FormatFileSize(displaySize)} · {actionHint}" : (isMedia ? "▶ Нажмите, чтобы открыть" : "💾 Нажмите для загрузки"));

            // Прогресс загрузки: -1 = не идёт, 0..1 = доля. Рисуется поверх иконки.
            double dlProgress = -1;
            bool downloading = false;

            Color iconBg = ext switch
            {
                "pdf" => Color.FromArgb(220, 53, 53),
                "doc" or "docx" => Color.FromArgb(41, 86, 163),
                "xls" or "xlsx" => Color.FromArgb(32, 120, 62),
                "ppt" or "pptx" => Color.FromArgb(198, 69, 30),
                "zip" or "rar" or "7z" or "tar" or "gz" => Color.FromArgb(140, 90, 20),
                "txt" or "rtf" => Color.FromArgb(80, 80, 80),
                _ => Color.FromArgb(88, 101, 242),
            };

            int cardW = Math.Min(maxW, 280);
            var card = new Panel
            {
                Size = new Size(cardW, 56),
                BackColor = isMine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(47, 49, 54),
                Cursor = Cursors.Hand
            };

            var iconPnl = new Panel { Size = new Size(40, 40), Location = new Point(8, 8), BackColor = Color.Transparent };
            iconPnl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillRoundedRectangle(new SolidBrush(iconBg), 0, 0, 39, 39, 6);
                string lbl = ext.Length > 0 ? ext.ToUpper() : "?";
                using var f = new Font("Segoe UI Black", lbl.Length > 3 ? 6.5f : 8.5f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(lbl, f, Brushes.White, new RectangleF(0, 0, 40, 40), sf);

                // Круговой индикатор загрузки поверх иконки.
                if (dlProgress >= 0)
                {
                    using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                    e.Graphics.FillRoundedRectangle(shade, 0, 0, 39, 39, 6);
                    var rect = new Rectangle(8, 8, 23, 23);
                    using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 3);
                    using var arc = new Pen(Color.White, 3);
                    e.Graphics.DrawEllipse(track, rect);
                    e.Graphics.DrawArc(arc, rect, -90, (float)(360 * Math.Min(1.0, dlProgress)));
                }
            };
            card.Controls.Add(iconPnl);

            var lblName = new Label
            {
                Text = fileName,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 8),
                Size = new Size(cardW - 64, 20),
                AutoEllipsis = true
            };
            card.Controls.Add(lblName);

            var lblSz = new Label
            {
                Text = szStr,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 28),
                AutoSize = true
            };
            card.Controls.Add(lblSz);

            void SaveAndOpen()
            {
                using var save = new SaveFileDialog { FileName = fileName, Title = "Сохранить файл" };
                if (save.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllBytes(save.FileName, fileData);
                    try { Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true }); }
                    catch { }
                }
            }

            // Видео/музыку открываем во встроенном проигрывателе (перемотка/громкость),
            // остальные файлы — сохраняем и открываем системно.
            void OpenIt()
            {
                if (isMedia && fileData != null)
                {
                    try { new MediaPlayerForm(fileData, fileName, isVideoMedia).Show(); }
                    catch { SaveAndOpen(); }
                }
                else SaveAndOpen();
            }

            void DoOpen(object s, EventArgs ev)
            {
                if (downloading) return;

                if (fileData != null) { OpenIt(); return; }

                // Кеш — мгновенно.
                if (MediaCache.Has(msgId, "file", fileName))
                {
                    fileData = MediaCache.Get(msgId, "file", fileName);
                    if (fileData != null) { lblSz.Text = FormatFileSize(fileData.Length); OpenIt(); return; }
                }

                // Чанковая загрузка с сервера с круговым индикатором прогресса.
                downloading = true;
                dlProgress = 0;
                lblSz.Text = "Загрузка… 0%";
                try { iconPnl.Invalidate(); } catch { }

                string table = isGroup ? "group_messages" : "messages";
                System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] result = null;
                    string err = null;
                    try
                    {
                        using var conn = DBHelper.OpenConnection();

                        long total = knownSize;
                        if (total <= 0)
                        {
                            using var szCmd = new MySqlCommand($"SELECT OCTET_LENGTH(file_data) FROM {table} WHERE id=@id", conn);
                            szCmd.Parameters.AddWithValue("@id", msgId);
                            var o = szCmd.ExecuteScalar();
                            total = (o != null && o != DBNull.Value) ? Convert.ToInt64(o) : 0;
                        }

                        if (total <= 0) { err = "Файл пуст"; }
                        else
                        {
                            const int CHUNK = 256 * 1024; // 256 КБ
                            using var ms = new MemoryStream((int)Math.Min(total, int.MaxValue));
                            long off = 0;
                            while (off < total)
                            {
                                int len = (int)Math.Min(CHUNK, total - off);
                                // MySQL SUBSTRING — 1-based смещение.
                                using var cmd = new MySqlCommand(
                                    $"SELECT SUBSTRING(file_data, @off, @len) FROM {table} WHERE id=@id", conn);
                                cmd.Parameters.AddWithValue("@off", off + 1);
                                cmd.Parameters.AddWithValue("@len", len);
                                cmd.Parameters.AddWithValue("@id", msgId);
                                var chunk = cmd.ExecuteScalar() as byte[];
                                if (chunk == null || chunk.Length == 0) break;
                                ms.Write(chunk, 0, chunk.Length);
                                off += chunk.Length;

                                double p = (double)off / total;
                                try
                                {
                                    card.BeginInvoke(() =>
                                    {
                                        dlProgress = p;
                                        lblSz.Text = $"Загрузка… {(int)(p * 100)}%";
                                        try { iconPnl.Invalidate(); } catch { }
                                    });
                                }
                                catch { }
                            }
                            result = ms.ToArray();
                        }
                    }
                    catch (Exception ex) { err = ex.Message; }

                    try
                    {
                        card.BeginInvoke(() =>
                        {
                            downloading = false;
                            dlProgress = -1;
                            try { iconPnl.Invalidate(); } catch { }

                            if (result != null && result.Length > 0)
                            {
                                fileData = result;
                                MediaCache.Put(msgId, "file", fileData, fileName);
                                lblSz.Text = FormatFileSize(fileData.Length);
                                OpenIt();
                            }
                            else
                            {
                                lblSz.Text = "Ошибка: " + (err ?? "нет данных");
                            }
                        });
                    }
                    catch { }
                });
            }

            card.Click += DoOpen;
            foreach (Control c in card.Controls) c.Click += DoOpen;

            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new SolidBrush(card.BackColor);
                e.Graphics.FillRoundedRectangle(br, 0, 0, card.Width - 1, card.Height - 1, 8);
                card.Region = System.Drawing.Region.FromHrgn(
                    NativeMethods.CreateRoundRectRgn(0, 0, card.Width, card.Height, 8, 8));
            };

            return card;
        }

        /// <summary>Очищает панель сообщений С УНИЧТОЖЕНием дочерних контролов.
        /// Critically: Controls.Clear() НЕ вызывает Dispose, из-за чего встроенные
        /// видео-плееры (WebView2) продолжали играть звук в фоне после смены чата.</summary>
        internal static void DisposeAndClear(Control parent)
        {
            if (parent == null) return;
            for (int i = parent.Controls.Count - 1; i >= 0; i--)
            {
                try { parent.Controls[i].Dispose(); } catch { }
            }
            try { parent.Controls.Clear(); } catch { }
        }

        internal static void ShowImageFullscreen(byte[] imgBytes)
        {
            var v = new Form
            {
                Text = "Просмотр",
                Size = new Size(900, 700),
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.Black
            };
            var pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            // БЕЗ using — ms должен жить пока живёт форма
            var ms = new MemoryStream(imgBytes.ToArray());
            var img = Image.FromStream(ms);
            pb.Image = img;

            v.Controls.Add(pb);

            // Освобождаем только при закрытии формы
            v.FormClosed += (s, e) => { img.Dispose(); ms.Dispose(); };

            v.Show();
        }

        // Ctrl+V — вставить изображение или файл из буфера
        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    _pendingImageBytes = ms.ToArray();
                    _pendingAttach = new PendingAttachment(_pendingImageBytes, "image.png", AttachKind.Image);
                    ShowPreview(_pendingAttach);
                    e.Handled = true;
                }
                return;
            }

            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    string path = files[0];
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 64L * 1024 * 1024)
                        {
                            MessageBox.Show("Файл > 64 МБ.");
                            return;
                        }

                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        bool isImg = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext);
                        bool isGif = ext == ".gif";
                        var kind = isGif ? AttachKind.Gif : isImg ? AttachKind.Image : AttachKind.File;

                        if (isImg && bytes.Length > 2 * 1024 * 1024)
                            bytes = CompressImageIfNeeded(bytes);

                        _pendingAttach = new PendingAttachment(bytes, Path.GetFileName(path), kind);
                        ShowPreview(_pendingAttach);
                        e.Handled = true;
                    }
                    catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message); }
                }
            }
        }

        private void txtMessage_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                btnSend_Click(this, EventArgs.Empty);
                txtMessage.Clear();
                return;
            }
            e.IsInputKey = false;
        }

        // ════════════════════════════════════════════════════════════════
        //  КНОПКИ УПРАВЛЕНИЯ
        // ════════════════════════════════════════════════════════════════
        private void btnRefresh_Click(object sender, EventArgs e)
        {
            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();

            if (_currentGroupId >= 0)
                LoadGroupMessages();
            else if (_currentChatPartnerId >= 0)
                LoadMessages();

            PollTick(null, null); // разовый опрос непрочитанных/новых (как делал таймер)
        }

        private void btnSettings_Click(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();

            if (UserSession.IsImpersonating)
                menu.Items.Add("← Вернуться к своему аккаунту", null, (s, ev) =>
                {
                    UserSession.StopImpersonating();
                    lblCurrentUser.Text = UserSession.UserName;
                    RemoveExitImpersonateButton();
                    ClearChat();
                    LoadAllUsersForAdmin();
                });

            menu.Items.Add("👤 Редактировать профиль", null, (s, ev) => OpenProfile());

            menu.Items.Add("🔑 Сменить пароль", null, (s, ev) =>
                new ChangePasswordForm().ShowDialog(this));

            menu.Items.Add("🎛 Настройки устройств (камера/микрофон)", null, (s, ev) =>
                new SettingsForm().ShowDialog(this));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("🗑 Очистить кеш (переписка + медиа)", null, (s, ev) => ClearAllCaches());

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("🚪 Выйти из аккаунта", null, (s, ev) =>
            {
                _pollTimer.Stop();
                _trayIcon.Visible = false;
                UserSession.Clear();
                this.Close();
            });

            menu.Show(btnSettings, new Point(0, -menu.Items.Count * 28));
        }

        // ════════════════════════════════════════════════════════════════
        //  ВСПОМОГАТЕЛЬНОЕ
        // ════════════════════════════════════════════════════════════════
        private void MarkAsRead(int senderId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE messages SET is_read=1 WHERE sender_id=@s AND receiver_id=@r AND is_read=0",
                    conn);
                cmd.Parameters.AddWithValue("@s", senderId);
                cmd.Parameters.AddWithValue("@r", UserSession.EffectiveId);
                int affected = cmd.ExecuteNonQuery();

                // Сообщаем отправителю по WS, что его сообщения прочитаны — чтобы у
                // него галочки стали «прочитано» сразу, без переоткрытия чата.
                if (affected > 0)
                    try { WebSocketSignalingClient.Instance.SendMessage("read", 0, UserSession.EffectiveId, "direct"); } catch { }

                foreach (var p in _userPanels)
                    if (p.Tag is int id && id == senderId)
                    {
                        foreach (Control c in p.Controls.Cast<Control>().ToList())
                            if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                                p.Controls.Remove(c);
                    }
            }
            catch { }
        }

        private static string BuildName(object name, object surname, object login)
        {
            string full = $"{name} {surname}".Trim();
            return string.IsNullOrWhiteSpace(full) ? login.ToString() : full;
        }

        private Color GetAvatarColor(int uid)
        {
            Color[] palette =
            {
                Color.FromArgb(88,  101, 242),
                Color.FromArgb(87,  171, 90),
                Color.FromArgb(240, 71,  71),
                Color.FromArgb(250, 166, 26),
                Color.FromArgb(0,   176, 244),
                Color.FromArgb(235, 69,  158),
                Color.FromArgb(98,  200, 218),
                Color.FromArgb(156, 89,  182),
            };
            return palette[Math.Abs(uid) % palette.Length];
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _pollTimer?.Stop();
            _presenceTimer?.Stop();
            MarkSelfOffline();
            _trayIcon.Visible = false;
            _waveIn?.Dispose();
            _waveOut?.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>Помечает себя «не в сети» при выходе (best-effort), чтобы
        /// собеседники сразу увидели офлайн, не дожидаясь таймаута heartbeat.</summary>
        private void MarkSelfOffline()
        {
            if (!_presenceColumnsOk) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen = DATE_SUB(NOW(), INTERVAL 1 HOUR) WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // Обработчики событий, на которые ссылается Designer.
        private void btnSend_MouseEnter(object sender, EventArgs e)
        {
            // Визуальный эффект наведения (можно изменить по вкусу)
            try
            {
                btnSend.BackColor = Color.FromArgb(100, 115, 255);
            }
            catch { /* safe-guard если btnSend ещё не инициализирован */ }
        }

        private void btnSend_MouseLeave(object sender, EventArgs e)
        {
            try
            {
                btnSend.BackColor = Color.FromArgb(88, 101, 242);
            }
            catch { }
        }

        private void pnlInputBar_Resize(object sender, EventArgs e)
        {
            // Поддерживаем корректное расположение/ширину полей при изменении ширины панели ввода.
            try
            {
                const int leftOffset = 146; // соответствует расположению txtMessage в Designer
                const int margin = 12;
                // Перемещаем кнопку отправки к правому краю панели
                var btnY = btnSend.Location.Y;
                btnSend.Location = new Point(Math.Max(margin, pnlInputBar.ClientSize.Width - btnSend.Width - margin), btnY);

                // Кнопка GIF — слева от «Отправить».
                int rightEdge = btnSend.Location.X;
                if (btnGif != null)
                {
                    btnGif.Location = new Point(btnSend.Location.X - btnGif.Width - 6, btnY);
                    rightEdge = btnGif.Location.X;
                }

                // Меняем ширину txtMessage так, чтобы не перекрываться с кнопками
                int newWidth = rightEdge - margin - leftOffset;
                if (newWidth < 60) newWidth = 60;
                txtMessage.Size = new Size(newWidth, txtMessage.Height);
            }
            catch { }
        }

        private void pnlMessages_Resize(object sender, EventArgs e)
        {
            // Вызываем существующий метод обновления позиций пузырей.
            RefreshBubblePositions();
        }
    }

    // ── P/Invoke для скруглённых углов ───────────────────────────────
    internal static class NativeMethods
    {
        // ── Gdi32: скруглённые углы ───────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("Gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        // ── User32: цвет прогресс-бара (для VideoCircleRecordForm) ────────
        public enum PbColor { Green = 1, Red = 2, Yellow = 3 }

        public static void SetProgressBarColor(ProgressBar pb, PbColor color)
        {
            try { SendMessage(pb.Handle, 0x0410, (IntPtr)(int)color, IntPtr.Zero); }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }

    // ── Extension для скруглённых прямоугольников ─────────────────────
    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush br,
            float x, float y, float w, float h, float r)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(br, path);
        }
    }
    
}