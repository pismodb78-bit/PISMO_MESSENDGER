using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Окно управления участниками группового чата.
    /// Добавление участников доступно всем участникам группы.
    /// Исключение участников доступно только системному администратору PISMO.
    /// </summary>
    public class GroupMembersForm : Form
    {
        private readonly int _groupId;
        private readonly string _groupName;

        private FlowLayoutPanel _pnlMembers;
        private Button _btnAddMembers;
        private Label _lblTitle;

        /// <summary>true, если состав группы изменился (нужно перезагрузить сайдбар).</summary>
        public bool Changed { get; private set; } = false;

        public GroupMembersForm(int groupId, string groupName)
        {
            _groupId = groupId;
            _groupName = groupName;

            BuildUi();
            LoadMembers();
        }

        // ════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text            = "PISMO — Участники группы";
            ClientSize      = new Size(400, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            _lblTitle = new Label
            {
                Text      = $"👥 {_groupName}",
                Font      = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            var lblHint = new Label
            {
                Text      = "УЧАСТНИКИ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 54)
            };

            _pnlMembers = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = Color.FromArgb(47, 49, 54),
                Location      = new Point(20, 76),
                Size          = new Size(360, 350),
                Padding       = new Padding(4)
            };

            _btnAddMembers = new Button
            {
                Text      = "➕ Добавить участников",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 440),
                Size      = new Size(360, 42),
                Cursor    = Cursors.Hand
            };
            _btnAddMembers.FlatAppearance.BorderSize = 0;
            _btnAddMembers.Click += BtnAddMembers_Click;
            _btnAddMembers.MouseEnter += (s, e) => _btnAddMembers.BackColor = Color.FromArgb(71, 82, 196);
            _btnAddMembers.MouseLeave += (s, e) => _btnAddMembers.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[] { _lblTitle, lblHint, _pnlMembers, _btnAddMembers });
        }

        // ════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА УЧАСТНИКОВ
        // ════════════════════════════════════════════════════════════
        private void LoadMembers()
        {
            _pnlMembers.Controls.Clear();

            bool canKick = UserSession.Role == "admin"; // только системный администратор PISMO

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = @"
                    SELECT u.id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS full_name, u.login, gm.is_admin
                    FROM group_members gm
                    JOIN users u ON u.id = gm.user_id
                    WHERE gm.group_id=@g
                    ORDER BY gm.is_admin DESC, full_name ASC";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@g", _groupId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                _lblTitle.Text = $"👥 {_groupName} ({dt.Rows.Count})";

                foreach (DataRow row in dt.Rows)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string name = row["full_name"].ToString();
                    if (string.IsNullOrWhiteSpace(name)) name = row["login"].ToString();
                    bool isGroupAdmin = Convert.ToBoolean(row["is_admin"]);

                    _pnlMembers.Controls.Add(BuildMemberRow(uid, name, isGroupAdmin, canKick));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки участников: " + ex.Message);
            }
        }

        private Panel BuildMemberRow(int uid, string name, bool isGroupAdmin, bool canKick)
        {
            var row = new Panel
            {
                Size = new Size(_pnlMembers.Width - 24, 44),
                Margin = new Padding(0, 0, 0, 2),
                BackColor = Color.FromArgb(54, 57, 63)
            };

            var avatar = new Panel { Size = new Size(32, 32), Location = new Point(8, 6) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(new SolidBrush(GetAvatarColor(uid)),
                    0, 0, avatar.Width - 1, avatar.Height - 1);
                string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                using var f = new Font("Segoe UI Black", 12f, FontStyle.Bold);
                var sz = e.Graphics.MeasureString(letter, f);
                e.Graphics.DrawString(letter, f, Brushes.White,
                    (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
            };

            var lblName = new Label
            {
                Text = isGroupAdmin ? $"{name}  ⭐" : name,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                AutoSize = false,
                Location = new Point(48, 0),
                Size = new Size(row.Width - 48 - (canKick ? 90 : 12), row.Height),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };

            row.Controls.Add(avatar);
            row.Controls.Add(lblName);

            if (canKick)
            {
                var btnKick = new Button
                {
                    Text = "Исключить",
                    BackColor = Color.FromArgb(240, 71, 71),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    Size = new Size(84, 30),
                    Location = new Point(row.Width - 92, 7),
                    Cursor = Cursors.Hand
                };
                btnKick.FlatAppearance.BorderSize = 0;
                btnKick.Click += (s, e) => KickMember(uid, name);
                row.Controls.Add(btnKick);
            }

            return row;
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

        // ════════════════════════════════════════════════════════════
        //  ИСКЛЮЧЕНИЕ УЧАСТНИКА (только системный admin)
        // ════════════════════════════════════════════════════════════
        private void KickMember(int uid, string name)
        {
            if (uid == UserSession.EffectiveId)
            {
                MessageBox.Show("Чтобы покинуть группу самостоятельно, используйте пункт «Покинуть группу».");
                return;
            }

            var confirm = MessageBox.Show(
                $"Исключить «{name}» из группы «{_groupName}»?",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@g", _groupId);
                cmd.Parameters.AddWithValue("@u", uid);
                cmd.ExecuteNonQuery();

                Changed = true;
                LoadMembers();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  ДОБАВЛЕНИЕ УЧАСТНИКОВ (доступно всем участникам группы)
        // ════════════════════════════════════════════════════════════
        private void BtnAddMembers_Click(object sender, EventArgs e)
        {
            using var dlg = new AddGroupMembersForm(_groupId);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.AddedCount > 0)
            {
                Changed = true;
                LoadMembers();
            }
        }
    }

    /// <summary>Диалог выбора пользователей для добавления в существующую группу.</summary>
    public class AddGroupMembersForm : Form
    {
        private readonly int _groupId;
        private CheckedListBox _clbUsers;
        private Label _lblError;
        private Button _btnAdd;

        private readonly Dictionary<int, string> _userMap = new();

        public int AddedCount { get; private set; } = 0;

        public AddGroupMembersForm(int groupId)
        {
            _groupId = groupId;
            BuildUi();
            LoadCandidates();
        }

        private void BuildUi()
        {
            Text            = "PISMO — Добавить участников";
            ClientSize      = new Size(360, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text      = "Добавить участников",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            _clbUsers = new CheckedListBox
            {
                BackColor    = Color.FromArgb(32, 34, 37),
                ForeColor    = Color.FromArgb(220, 221, 222),
                BorderStyle  = BorderStyle.FixedSingle,
                Font         = new Font("Segoe UI", 10f),
                Location     = new Point(20, 56),
                Size         = new Size(320, 300),
                CheckOnClick = true
            };

            _lblError = new Label
            {
                ForeColor = Color.FromArgb(240, 71, 71),
                Font      = new Font("Segoe UI", 9f),
                Location  = new Point(20, 362),
                Size      = new Size(320, 20),
                Visible   = false
            };

            _btnAdd = new Button
            {
                Text      = "Добавить",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 388),
                Size      = new Size(320, 42),
                Cursor    = Cursors.Hand
            };
            _btnAdd.FlatAppearance.BorderSize = 0;
            _btnAdd.Click += BtnAdd_Click;
            _btnAdd.MouseEnter += (s, e) => _btnAdd.BackColor = Color.FromArgb(71, 82, 196);
            _btnAdd.MouseLeave += (s, e) => _btnAdd.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[] { lblTitle, _clbUsers, _lblError, _btnAdd });
        }

        private void LoadCandidates()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = @"
                    SELECT u.id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS full_name, u.login
                    FROM users u
                    WHERE u.id NOT IN (SELECT user_id FROM group_members WHERE group_id=@g)
                    ORDER BY full_name, u.login";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@g", _groupId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                if (dt.Rows.Count == 0)
                {
                    _lblError.Visible = true;
                    _lblError.ForeColor = Color.FromArgb(185, 187, 190);
                    _lblError.Text = "Все пользователи уже в этой группе.";
                    _btnAdd.Enabled = false;
                    _clbUsers.Enabled = false;
                    return;
                }

                foreach (DataRow row in dt.Rows)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string full = row["full_name"].ToString();
                    if (string.IsNullOrWhiteSpace(full)) full = row["login"].ToString();

                    _userMap[uid] = full;
                    _clbUsers.Items.Add(full);
                }
            }
            catch (Exception ex)
            {
                ShowError("Ошибка загрузки пользователей: " + ex.Message);
            }
        }

        private void ShowError(string msg)
        {
            _lblError.ForeColor = Color.FromArgb(240, 71, 71);
            _lblError.Text    = msg;
            _lblError.Visible = true;
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            var selectedIds = new List<int>();
            for (int i = 0; i < _clbUsers.Items.Count; i++)
            {
                if (_clbUsers.GetItemChecked(i))
                {
                    string label = _clbUsers.Items[i].ToString();
                    foreach (var kv in _userMap)
                        if (kv.Value == label) { selectedIds.Add(kv.Key); break; }
                }
            }

            if (selectedIds.Count == 0)
            {
                ShowError("Выберите хотя бы одного пользователя.");
                return;
            }

            try
            {
                using var conn = DBHelper.OpenConnection();
                foreach (int uid in selectedIds)
                {
                    using var cmd = new MySqlCommand(
                        "INSERT INTO group_members (group_id, user_id, is_admin) VALUES (@g, @u, 0)", conn);
                    cmd.Parameters.AddWithValue("@g", _groupId);
                    cmd.Parameters.AddWithValue("@u", uid);
                    cmd.ExecuteNonQuery();
                }

                AddedCount = selectedIds.Count;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("Ошибка БД: " + ex.Message);
            }
        }
    }
}
