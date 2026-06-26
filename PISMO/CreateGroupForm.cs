using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>Диалог создания группового чата: название + выбор участников.</summary>
    public class CreateGroupForm : Form
    {
        private TextBox _txtName;
        private CheckedListBox _clbUsers;
        private Label _lblError;
        private Button _btnCreate;

        /// <summary>ID созданной группы (после успешного OK).</summary>
        public int CreatedGroupId { get; private set; } = -1;
        public string CreatedGroupName { get; private set; } = "";

        private readonly Dictionary<int, string> _userMap = new();

        public CreateGroupForm()
        {
            BuildUi();
            LoadUsers();
        }

        private void BuildUi()
        {
            Text            = "PISMO — Новая группа";
            ClientSize      = new Size(380, 460);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text      = "Новая группа",
                Font      = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 18)
            };

            var lblNameHint = new Label
            {
                Text      = "НАЗВАНИЕ ГРУППЫ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 58)
            };

            _txtName = new TextBox
            {
                BackColor   = Color.FromArgb(32, 34, 37),
                ForeColor   = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 11f),
                Location    = new Point(20, 76),
                Size        = new Size(340, 32)
            };

            var lblUsersHint = new Label
            {
                Text      = "УЧАСТНИКИ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 120)
            };

            _clbUsers = new CheckedListBox
            {
                BackColor   = Color.FromArgb(32, 34, 37),
                ForeColor   = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 10f),
                Location    = new Point(20, 140),
                Size        = new Size(340, 220),
                CheckOnClick = true
            };

            _lblError = new Label
            {
                ForeColor = Color.FromArgb(240, 71, 71),
                Font      = new Font("Segoe UI", 9f),
                Location  = new Point(20, 368),
                Size      = new Size(340, 20),
                Visible   = false
            };

            _btnCreate = new Button
            {
                Text      = "Создать группу",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 396),
                Size      = new Size(340, 42),
                Cursor    = Cursors.Hand
            };
            _btnCreate.FlatAppearance.BorderSize = 0;
            _btnCreate.Click += BtnCreate_Click;
            _btnCreate.MouseEnter += (s, e) => _btnCreate.BackColor = Color.FromArgb(71, 82, 196);
            _btnCreate.MouseLeave += (s, e) => _btnCreate.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[]
            {
                lblTitle, lblNameHint, _txtName,
                lblUsersHint, _clbUsers, _lblError, _btnCreate
            });
        }

        private void LoadUsers()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "SELECT id, Name, Surname, login FROM users WHERE id <> @me ORDER BY Name, Surname";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                foreach (DataRow row in dt.Rows)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string full = $"{row["Name"]} {row["Surname"]}".Trim();
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
            _lblError.Text    = msg;
            _lblError.Visible = true;
        }

        private void BtnCreate_Click(object sender, EventArgs e)
        {
            string name = _txtName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("Введите название группы.");
                return;
            }

            var selectedIds = new List<int>();
            for (int i = 0; i < _clbUsers.Items.Count; i++)
            {
                if (_clbUsers.GetItemChecked(i))
                {
                    // Сопоставляем по тексту обратно к id (порядок совпадает с _userMap при заполнении)
                    string label = _clbUsers.Items[i].ToString();
                    foreach (var kv in _userMap)
                        if (kv.Value == label) { selectedIds.Add(kv.Key); break; }
                }
            }

            if (selectedIds.Count == 0)
            {
                ShowError("Выберите хотя бы одного участника.");
                return;
            }

            int myId = UserSession.EffectiveId;

            try
            {
                using var conn = DBHelper.OpenConnection();

                int groupId;
                using (var cmdIns = new MySqlCommand(
                    "INSERT INTO group_chats (name, created_by) VALUES (@n, @c)", conn))
                {
                    cmdIns.Parameters.AddWithValue("@n", name);
                    cmdIns.Parameters.AddWithValue("@c", myId);
                    cmdIns.ExecuteNonQuery();
                    groupId = (int)cmdIns.LastInsertedId;
                }

                using (var cmdMe = new MySqlCommand(
                    "INSERT INTO group_members (group_id, user_id, is_admin) VALUES (@g, @u, 1)", conn))
                {
                    cmdMe.Parameters.AddWithValue("@g", groupId);
                    cmdMe.Parameters.AddWithValue("@u", myId);
                    cmdMe.ExecuteNonQuery();
                }

                foreach (int uid in selectedIds)
                {
                    using var cmdMember = new MySqlCommand(
                        "INSERT INTO group_members (group_id, user_id, is_admin) VALUES (@g, @u, 0)", conn);
                    cmdMember.Parameters.AddWithValue("@g", groupId);
                    cmdMember.Parameters.AddWithValue("@u", uid);
                    cmdMember.ExecuteNonQuery();
                }

                CreatedGroupId   = groupId;
                CreatedGroupName = name;
                DialogResult     = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("Ошибка БД: " + ex.Message);
            }
        }
    }
}
