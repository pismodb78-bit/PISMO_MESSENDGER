using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Окно «Добавить друга»: поиск пользователей по имени или логину (@имя/#имя)
    /// и добавление/удаление из друзей. После изменений выставляет Changed=true,
    /// чтобы вызывающая форма перезагрузила список контактов.
    /// </summary>
    public sealed class FriendsAddForm : Form
    {
        private readonly int _me;
        private readonly TextBox _search;
        private readonly FlowLayoutPanel _results;
        private readonly Label _hint;

        /// <summary>Были ли изменения (добавлен/удалён друг) — чтобы обновить список.</summary>
        public bool Changed { get; private set; }

        public FriendsAddForm(int me)
        {
            _me = me;
            Text = "Добавить друга";
            ClientSize = new Size(420, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = Color.FromArgb(54, 57, 63);
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = "Добавить друга",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14)
            };

            _search = new TextBox
            {
                Location = new Point(16, 50),
                Size = new Size(388, 28),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f),
                PlaceholderText = "Логин (@имя / #имя) или имя"
            };
            _search.TextChanged += (s, e) => RunSearch();

            _hint = new Label
            {
                Text = "Введите логин или имя, чтобы найти пользователя.",
                ForeColor = Color.FromArgb(150, 152, 158),
                AutoSize = false,
                Size = new Size(388, 18),
                Location = new Point(16, 84),
                Font = new Font("Segoe UI", 8.5f)
            };

            _results = new FlowLayoutPanel
            {
                Location = new Point(10, 110),
                Size = new Size(400, 398),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Color.FromArgb(54, 57, 63)
            };

            Controls.Add(title);
            Controls.Add(_search);
            Controls.Add(_hint);
            Controls.Add(_results);
        }

        private void RunSearch()
        {
            string q = _search.Text;
            _results.Controls.Clear();
            if (string.IsNullOrWhiteSpace(q))
            {
                _hint.Text = "Введите логин или имя, чтобы найти пользователя.";
                return;
            }
            _hint.Text = "Поиск…";

            System.Threading.Tasks.Task.Run(() =>
            {
                var hits = FriendsRepository.Search(_me, q);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        // Пока запрос менялся, показываем результат только для актуального текста.
                        if (_search.Text != q) return;
                        _results.Controls.Clear();
                        _hint.Text = hits.Count == 0 ? "Никого не найдено." : $"Найдено: {hits.Count}";
                        foreach (var h in hits) _results.Controls.Add(MakeRow(h));
                    }));
                }
                catch { }
            });
        }

        private Control MakeRow(FriendsRepository.UserHit h)
        {
            var card = new Panel
            {
                Width = 380,
                Height = 54,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = Color.FromArgb(47, 49, 54)
            };

            var lblName = new Label
            {
                Text = h.Name,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(250, 20),
                Location = new Point(12, 8),
                AutoEllipsis = true
            };
            var lblLogin = new Label
            {
                Text = string.IsNullOrWhiteSpace(h.Login) ? "" : "@" + h.Login,
                ForeColor = Color.FromArgb(150, 152, 158),
                AutoSize = false,
                Size = new Size(250, 18),
                Location = new Point(12, 28),
                Font = new Font("Segoe UI", 8.5f),
                AutoEllipsis = true
            };

            var btn = new Button
            {
                Size = new Size(96, 30),
                Location = new Point(276, 12),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;

            void Render()
            {
                if (h.IsFriend)
                {
                    btn.Text = "➖ Убрать";
                    btn.BackColor = Color.FromArgb(64, 68, 75);
                }
                else
                {
                    btn.Text = "➕ Добавить";
                    btn.BackColor = Color.FromArgb(59, 165, 93);
                }
            }
            Render();

            btn.Click += (s, e) =>
            {
                if (h.IsFriend) FriendsRepository.Remove(_me, h.Id);
                else FriendsRepository.Add(_me, h.Id);
                h.IsFriend = !h.IsFriend;
                Changed = true;
                Render();
            };

            card.Controls.Add(lblName);
            card.Controls.Add(lblLogin);
            card.Controls.Add(btn);
            return card;
        }
    }
}
