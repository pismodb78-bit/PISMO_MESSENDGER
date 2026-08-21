using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    public class SelectCallParticipantsForm : Form
    {
        private CheckedListBox _clb;
        private Button _btnAll;
        private Button _btnAlpha;
        private Button _btnOk;
        private Button _btnCancel;
        public List<int> SelectedUserIds { get; private set; } = new();

        public SelectCallParticipantsForm(int groupId)
        {
            Text = "Выберите участников звонка";
            ClientSize = new Size(360, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(54, 57, 63);
            Font = new Font("Segoe UI", 9f);

            _clb = new CheckedListBox
            {
                Location = new Point(12, 12),
                Size = new Size(336, 320),
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.FixedSingle
            };

            _btnAll = new Button { Text = "Все", Location = new Point(12, 340), Size = new Size(80, 30) };
            _btnAlpha = new Button { Text = "По алфавиту", Location = new Point(100, 340), Size = new Size(120, 30) };
            _btnOk = new Button { Text = "Позвонить", Location = new Point(232, 340), Size = new Size(116, 30) };
            _btnCancel = new Button { Text = "Отмена", Location = new Point(232, 376), Size = new Size(116, 30) };

            _btnAll.Click += (s, e) =>
            {
                for (int i = 0; i < _clb.Items.Count; i++) _clb.SetItemChecked(i, true);
            };
            _btnAlpha.Click += (s, e) =>
            {
                // Пересортировать по тексту
                var items = new List<(string Text, int Id)>();
                foreach (var o in _clb.Items)
                {
                    if (o is ListItem li) items.Add((li.Text, li.Id));
                }
                items.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.InvariantCultureIgnoreCase));
                _clb.Items.Clear();
                foreach (var it in items) _clb.Items.Add(new ListItem(it.Id, it.Text), true);
            };
            _btnOk.Click += (s, e) => { AcceptSelection(); };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { _clb, _btnAll, _btnAlpha, _btnOk, _btnCancel });

            LoadGroupMembers(groupId);
        }

        private void AcceptSelection()
        {
            SelectedUserIds.Clear();
            foreach (var idx in _clb.CheckedIndices)
            {
                if (idx is int i && _clb.Items[i] is ListItem li)
                    SelectedUserIds.Add(li.Id);
            }
            if (SelectedUserIds.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одного участника.", "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void LoadGroupMembers(int groupId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT gm.user_id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS fullname, u.login " +
                    "FROM group_members gm JOIN users u ON u.id = gm.user_id WHERE gm.group_id=@g", conn);
                cmd.Parameters.AddWithValue("@g", groupId);
                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int id = Convert.ToInt32(r["user_id"]);
                    string name = r["fullname"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(name)) name = r["login"].ToString();
                    _clb.Items.Add(new ListItem(id, name), true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки участников: " + ex.Message);
            }
        }

        private class ListItem
        {
            public int Id { get; }
            public string Text { get; }
            public ListItem(int id, string text) { Id = id; Text = text; }
            public override string ToString() => Text;
        }
    }
}
