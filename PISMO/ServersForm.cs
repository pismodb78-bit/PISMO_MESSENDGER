using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Серверы как в Discord: список серверов, текстовые и голосовые каналы,
    /// чат в текстовом канале, подключение к голосовому (LiveKit-комната
    /// "vch_<channelId>"), участники с базовым управлением (кик/бан для владельца).
    /// Роли с правами и @упоминания — следующая итерация (таблицы уже заведены).
    /// </summary>
    public sealed class ServersForm : Form
    {
        private int _serverId = -1;
        private bool _isOwner = false;
        private int _channelId = -1;
        private string _channelType = "text";
        private string _channelName = "";

        private readonly int _me = UserSession.EffectiveId;

        private FlowLayoutPanel _pnlServers;
        private FlowLayoutPanel _pnlChannels;
        private FlowLayoutPanel _pnlMembers;
        private FlowLayoutPanel _pnlMessages;
        private Panel _pnlInput;
        private TextBox _txtInput;
        private Label _lblTitle;
        private System.Windows.Forms.Timer _refresh;
        private int _lastMsgCount = -1;

        public ServersForm()
        {
            Text = "PISMO — Серверы";
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(54, 57, 63);
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Load += (s, e) => LoadServers();

            _refresh = new System.Windows.Forms.Timer { Interval = 2500 };
            _refresh.Tick += (s, e) => { if (_channelId > 0 && _channelType == "text") MaybeReloadMessages(); };
            _refresh.Start();

            WebSocketSignalingClient.Instance.OnMessageReceived += OnWs;
            FormClosed += (s, e) =>
            {
                try { WebSocketSignalingClient.Instance.OnMessageReceived -= OnWs; } catch { }
                try { _refresh.Stop(); _refresh.Dispose(); } catch { }
            };
        }

        private void OnWs(string type, int senderId, int sessionId, string payload)
        {
            if (type == "new_message" && payload == "server" && sessionId == _channelId)
            {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(LoadMessages)); } catch { }
            }
        }

        private void BuildUi()
        {
            _pnlServers = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 180, BackColor = Color.FromArgb(32, 34, 37), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            _pnlChannels = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 200, BackColor = Color.FromArgb(47, 49, 54), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            _pnlMembers = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 180, BackColor = Color.FromArgb(47, 49, 54), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };

            var center = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(54, 57, 63) };
            _lblTitle = new Label { Dock = DockStyle.Top, Height = 36, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Text = "Выберите канал" };
            _pnlMessages = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(54, 57, 63), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };

            _pnlInput = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(64, 68, 75), Visible = false };
            _txtInput = new TextBox { Dock = DockStyle.Fill, Multiline = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, Font = new Font("Segoe UI", 11f) };
            _txtInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendChannelMessage(); } };
            var btnSend = new Button { Dock = DockStyle.Right, Width = 90, Text = "Отправить", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White };
            btnSend.FlatAppearance.BorderSize = 0;
            btnSend.Click += (s, e) => SendChannelMessage();
            var inputHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 10) };
            inputHost.Controls.Add(_txtInput);
            _pnlInput.Controls.Add(inputHost);
            _pnlInput.Controls.Add(btnSend);

            center.Controls.Add(_pnlMessages);
            center.Controls.Add(_pnlInput);
            center.Controls.Add(_lblTitle);

            Controls.Add(center);
            Controls.Add(_pnlMembers);
            Controls.Add(_pnlChannels);
            Controls.Add(_pnlServers);
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
                    b.Click += (s, e) => SelectServer(sid, name, owner);
                    _pnlServers.Controls.Add(b);
                }
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

        private void SelectServer(int sid, string name, bool owner)
        {
            _serverId = sid; _isOwner = owner; _channelId = -1; _lastMsgCount = -1;
            _lblTitle.Text = name;
            _pnlMessages.Controls.Clear();
            _pnlInput.Visible = false;
            LoadChannels();
            LoadMembers();
        }

        // ── Каналы ──────────────────────────────────────────────────────
        private void LoadChannels()
        {
            _pnlChannels.Controls.Clear();
            _pnlChannels.Controls.Add(MakeHeader("Каналы"));
            if (_isOwner)
            {
                var add = MakeSideButton("➕ Канал", Color.FromArgb(59, 165, 93));
                add.Click += (s, e) => CreateChannel();
                _pnlChannels.Controls.Add(add);
                var inv = MakeSideButton("ℹ ID сервера: " + _serverId, Color.FromArgb(47, 49, 54));
                _pnlChannels.Controls.Add(inv);
            }
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT id,name,type FROM server_channels WHERE server_id=@s ORDER BY position,id", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int cid = Convert.ToInt32(r["id"]);
                    string cname = r["name"].ToString();
                    string ctype = r["type"].ToString();
                    var b = MakeSideButton((ctype == "voice" ? "🔊 " : "# ") + cname, Color.FromArgb(54, 57, 63));
                    b.Click += (s, e) => SelectChannel(cid, ctype, cname);
                    _pnlChannels.Controls.Add(b);
                }
            }
            catch (Exception ex) { ShowDbError(ex); }
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

        private void SelectChannel(int cid, string type, string name)
        {
            _channelId = cid; _channelType = type; _channelName = name; _lastMsgCount = -1;
            _lblTitle.Text = (type == "voice" ? "🔊 " : "# ") + name;
            _pnlMessages.Controls.Clear();

            if (type == "voice")
            {
                _pnlInput.Visible = false;
                var join = new Button
                {
                    Text = "🔊 Подключиться к голосовому каналу",
                    Size = new Size(320, 44),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(59, 165, 93),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    Margin = new Padding(10, 20, 0, 0),
                    Cursor = Cursors.Hand
                };
                join.FlatAppearance.BorderSize = 0;
                join.Click += (s, e) =>
                {
                    var call = new CallForm("vch_" + cid, name);
                    call.Show();
                };
                _pnlMessages.Controls.Add(join);
            }
            else
            {
                _pnlInput.Visible = true;
                LoadMessages();
            }
        }

        // ── Сообщения канала ────────────────────────────────────────────
        private void MaybeReloadMessages()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT COUNT(*) FROM server_messages WHERE channel_id=@c", conn);
                cmd.Parameters.AddWithValue("@c", _channelId);
                int n = Convert.ToInt32(cmd.ExecuteScalar());
                if (n != _lastMsgCount) LoadMessages();
            }
            catch { }
        }

        private void LoadMessages()
        {
            if (_channelId <= 0 || _channelType != "text") return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT sm.sender_id, sm.text, sm.created_at, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login " +
                    "FROM server_messages sm JOIN users u ON u.id=sm.sender_id WHERE sm.channel_id=@c ORDER BY sm.id ASC", conn);
                cmd.Parameters.AddWithValue("@c", _channelId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);

                _pnlMessages.SuspendLayout();
                _pnlMessages.Controls.Clear();
                foreach (DataRow r in dt.Rows)
                {
                    string nm = r["nm"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"].ToString();
                    string text = Crypto.Dec(r["text"] == DBNull.Value ? "" : r["text"].ToString());
                    string time = Convert.ToDateTime(r["created_at"]).ToString("HH:mm");

                    var lbl = new Label
                    {
                        AutoSize = true,
                        MaximumSize = new Size(_pnlMessages.ClientSize.Width - 40, 0),
                        ForeColor = Color.FromArgb(220, 221, 222),
                        Margin = new Padding(0, 2, 0, 6),
                        Font = new Font("Segoe UI", 10f),
                        Text = $"{nm} · {time}\n{text}"
                    };
                    _pnlMessages.Controls.Add(lbl);
                }
                _lastMsgCount = dt.Rows.Count;
                _pnlMessages.ResumeLayout();
                _pnlMessages.ScrollControlIntoView(_pnlMessages.Controls.Count > 0 ? _pnlMessages.Controls[_pnlMessages.Controls.Count - 1] : null);
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private void SendChannelMessage()
        {
            string text = _txtInput.Text.Trim();
            if (string.IsNullOrEmpty(text) || _channelId <= 0) return;
            _txtInput.Clear();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("INSERT INTO server_messages (channel_id, sender_id, text) VALUES (@c,@s,@t)", conn);
                cmd.Parameters.AddWithValue("@c", _channelId);
                cmd.Parameters.AddWithValue("@s", _me);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(text));
                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _channelId, "server");
                LoadMessages();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        // ── Участники ───────────────────────────────────────────────────
        private void LoadMembers()
        {
            _pnlMembers.Controls.Clear();
            _pnlMembers.Controls.Add(MakeHeader("Участники"));
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.user_id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login, s.owner_id " +
                    "FROM server_members m JOIN users u ON u.id=m.user_id JOIN servers s ON s.id=m.server_id " +
                    "WHERE m.server_id=@s ORDER BY (m.user_id=s.owner_id) DESC, u.login", conn);
                cmd.Parameters.AddWithValue("@s", _serverId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int uid = Convert.ToInt32(r["user_id"]);
                    bool owner = Convert.ToInt32(r["owner_id"]) == uid;
                    string nm = r["nm"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = r["login"].ToString();
                    var b = MakeSideButton((owner ? "👑 " : "• ") + nm, Color.FromArgb(54, 57, 63));
                    if (_isOwner && uid != _me)
                    {
                        var menu = new ContextMenuStrip();
                        menu.Items.Add("Выгнать", null, (s, e) => KickMember(uid, false));
                        menu.Items.Add("Забанить", null, (s, e) => KickMember(uid, true));
                        b.ContextMenuStrip = menu;
                        b.Text += "  ⋮";
                    }
                    _pnlMembers.Controls.Add(b);
                }
            }
            catch (Exception ex) { ShowDbError(ex); }
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
