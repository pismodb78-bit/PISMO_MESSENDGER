using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Окно выбора, куда переслать сообщение: группы, личные чаты и каналы
    /// серверов — с заголовком на каждый сервер.
    ///
    /// Раньше на ПК выбора не было вовсе: пересылка показывала подсказку
    /// «выберите диалог и нажмите Отправить», и человек шёл искать нужный чат
    /// в общем списке сам. На телефоне окно есть, и переносим сюда именно его.
    ///
    /// Каналы сгруппированы по серверам не для красоты: канал «основной»
    /// создаётся на каждом сервере по умолчанию, и плоским списком получалось
    /// несколько одинаковых строк подряд, из которых не выбрать нужную.
    /// </summary>
    public sealed class ForwardPickerForm : Form
    {
        /// <summary>Куда переслали: 0 — личка, 1 — группа, 2 — канал сервера.</summary>
        public int TargetScope { get; private set; } = -1;
        public int TargetId { get; private set; } = -1;
        public string TargetName { get; private set; } = "";

        private readonly FlowLayoutPanel _list;

        public ForwardPickerForm()
        {
            Text = "PISMO — Переслать";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(420, 520);
            BackColor = Color.FromArgb(40, 42, 46);

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10, 10, 10, 10),
                BackColor = Color.FromArgb(40, 42, 46),
            };
            Controls.Add(_list);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.FromArgb(35, 37, 41) };
            var cancel = new Button
            {
                Text = "Отмена", FlatStyle = FlatStyle.Flat, Size = new Size(100, 30),
                Location = new Point(300, 7), BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White, Cursor = Cursors.Hand, DialogResult = DialogResult.Cancel,
            };
            cancel.FlatAppearance.BorderSize = 0;
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);
            CancelButton = cancel;

            try { ChatScroll.Attach(_list); } catch { }
            Load += (s, e) => Fill();
        }

        /// <summary>Цвет кружка-заглушки. Палитра та же, что в списках.</summary>
        private static Color AvatarColor(int seed)
        {
            Color[] palette =
            {
                Color.FromArgb(88, 101, 242), Color.FromArgb(87, 171, 90),
                Color.FromArgb(240, 71, 71),  Color.FromArgb(250, 166, 26),
                Color.FromArgb(0, 176, 244),  Color.FromArgb(235, 69, 158),
                Color.FromArgb(98, 200, 218), Color.FromArgb(156, 89, 182),
            };
            return palette[Math.Abs(seed) % palette.Length];
        }

        private Label Header(string text) => new Label
        {
            Text = text.ToUpperInvariant(),
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(114, 118, 125),
            AutoSize = false,
            Size = new Size(380, 22),
            Margin = new Padding(2, 10, 2, 2),
            TextAlign = ContentAlignment.BottomLeft,
        };

        /// <summary>Строка выбора: кружок, название, кнопка «Отправить».</summary>
        private Panel Row(int scope, int id, string title, bool userAvatar)
        {
            var row = new Panel
            {
                Size = new Size(380, 44),
                Margin = new Padding(2, 1, 2, 1),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
            };

            var av = new Panel { Size = new Size(32, 32), Location = new Point(2, 6), BackColor = Color.Transparent };
            av.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // У людей — настоящая аватарка, если она уже загружена; у групп и
                // каналов её не бывает, там кружок с буквой.
                if (userAvatar && AvatarStore.DrawAvatar(e.Graphics, id, 0, 0, 31)) return;
                if (userAvatar) AvatarStore.EnsureLoaded(id);
                using var br = new SolidBrush(AvatarColor(userAvatar ? id : id + 1000));
                e.Graphics.FillEllipse(br, 0, 0, 31, 31);
                string letter = string.IsNullOrWhiteSpace(title) ? "?" : title.Trim()[..1].ToUpperInvariant();
                TextRenderer.DrawText(e.Graphics, letter, new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                    new Rectangle(0, 0, 31, 31), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            row.Controls.Add(av);

            var lbl = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(220, 221, 222),
                AutoSize = false,
                Location = new Point(42, 12),
                Size = new Size(230, 20),
                AutoEllipsis = true,
            };
            row.Controls.Add(lbl);

            var send = new Button
            {
                Text = "Отправить", FlatStyle = FlatStyle.Flat, Size = new Size(94, 28),
                Location = new Point(280, 8), BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.5f),
            };
            send.FlatAppearance.BorderSize = 0;
            void Pick(object s, EventArgs e)
            {
                TargetScope = scope; TargetId = id; TargetName = title;
                DialogResult = DialogResult.OK;
                Close();
            }
            send.Click += Pick;
            row.Click += Pick;
            lbl.Click += Pick;
            av.Click += Pick;
            row.Controls.Add(send);

            void Hover(bool on) => row.BackColor = on ? Color.FromArgb(52, 55, 61) : Color.Transparent;
            row.MouseEnter += (s, e) => Hover(true);
            row.MouseLeave += (s, e) => Hover(false);
            foreach (Control c in row.Controls)
            {
                c.MouseEnter += (s, e) => Hover(true);
                c.MouseLeave += (s, e) => Hover(false);
            }
            return row;
        }

        private void Fill()
        {
            int me = UserSession.EffectiveId;
            try
            {
                using var conn = DBHelper.OpenConnection();

                // ── Группы ───────────────────────────────────────────────
                var groups = new List<(int id, string name)>();
                try
                {
                    using var cmd = new MySqlCommand(
                        "SELECT gc.id, gc.name FROM group_chats gc " +
                        "JOIN group_members mem ON mem.group_id = gc.id AND mem.user_id = @me " +
                        "ORDER BY gc.name", conn);
                    cmd.Parameters.AddWithValue("@me", me);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read()) groups.Add((Convert.ToInt32(rd["id"]), rd["name"].ToString()));
                }
                catch { }
                if (groups.Count > 0)
                {
                    _list.Controls.Add(Header("Группы"));
                    foreach (var g in groups) _list.Controls.Add(Row(1, g.id, g.name, userAvatar: false));
                }

                // ── Личные ───────────────────────────────────────────────
                var users = new List<(int id, string name)>();
                try
                {
                    using var cmd = new MySqlCommand(
                        "SELECT id, Name, Surname, login FROM users WHERE id <> @me ORDER BY Name", conn);
                    cmd.Parameters.AddWithValue("@me", me);
                    using var rd = cmd.ExecuteReader();
                    while (rd.Read())
                    {
                        string nm = (rd["Name"] + " " + rd["Surname"]).Trim();
                        if (string.IsNullOrWhiteSpace(nm)) nm = rd["login"].ToString();
                        users.Add((Convert.ToInt32(rd["id"]), nm));
                    }
                }
                catch { }
                if (users.Count > 0)
                {
                    _list.Controls.Add(Header("Личные"));
                    foreach (var u in users) _list.Controls.Add(Row(0, u.id, u.name, userAvatar: true));
                }

                // ── Каналы, по серверам ──────────────────────────────────
                try
                {
                    using var cmd = new MySqlCommand(
                        "SELECT ch.id, ch.name, s.name AS server_name " +
                        "FROM server_channels ch " +
                        "JOIN server_members m ON m.server_id = ch.server_id AND m.user_id = @me " +
                        "JOIN servers s ON s.id = ch.server_id " +
                        "ORDER BY s.name, ch.position, ch.id", conn);
                    cmd.Parameters.AddWithValue("@me", me);
                    using var rd = cmd.ExecuteReader();
                    string current = null;
                    while (rd.Read())
                    {
                        string srv = rd["server_name"].ToString();
                        if (srv != current) { _list.Controls.Add(Header(srv)); current = srv; }
                        _list.Controls.Add(Row(2, Convert.ToInt32(rd["id"]),
                            "# " + rd["name"], userAvatar: false));
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось получить список: " + ex.Message, "PISMO");
            }

            if (_list.Controls.Count == 0)
                _list.Controls.Add(new Label
                {
                    Text = "Некуда пересылать: нет ни групп, ни чатов, ни каналов.",
                    ForeColor = Color.FromArgb(150, 152, 158),
                    AutoSize = false, Size = new Size(380, 40),
                });
        }
    }
}
