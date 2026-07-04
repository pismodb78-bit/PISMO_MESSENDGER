using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Discord-подобное окно «Друзья»: вкладки «В сети / Все / Ожидание /
    /// Добавить в друзья». Приём и отправка заявок вынесены сюда (в списке чатов
    /// их больше нет). Здесь же — настройка «кто может мне писать» (Все / Только
    /// друзья). После изменений выставляет Changed=true, а при выборе «Написать»
    /// заполняет OpenChatWith.
    /// </summary>
    public sealed class FriendsAddForm : Form
    {
        private readonly int _me;

        private readonly Panel _tabBar;
        private readonly Panel _content;
        private readonly Button _btnPrivacy;
        private readonly TextBox _search;
        private readonly FlowLayoutPanel _list;
        private readonly Label _status;

        private enum Tab { Online, All, Pending, Add }
        private Tab _tab = Tab.Online;
        private readonly Dictionary<Tab, Button> _tabButtons = new();

        /// <summary>Были ли изменения (заявки/дружба) — чтобы обновить список чатов.</summary>
        public bool Changed { get; private set; }

        /// <summary>Если пользователь нажал «Написать» — id собеседника для открытия чата.</summary>
        public int? OpenChatWith { get; private set; }

        private static readonly Color Bg = Color.FromArgb(49, 51, 56);
        private static readonly Color Card = Color.FromArgb(43, 45, 49);
        private static readonly Color CardHover = Color.FromArgb(56, 58, 64);
        private static readonly Color Accent = Color.FromArgb(88, 101, 242);
        private static readonly Color Green = Color.FromArgb(59, 165, 93);
        private static readonly Color Neutral = Color.FromArgb(64, 68, 75);
        private static readonly Color Muted = Color.FromArgb(150, 152, 158);
        private static readonly Color OnlineDot = Color.FromArgb(59, 165, 93);
        private static readonly Color OfflineDot = Color.FromArgb(116, 127, 141);

        public FriendsAddForm(int me)
        {
            _me = me;
            Text = "Друзья";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(740, 460);
            BackColor = Bg;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ── Верхняя панель: заголовок + вкладки (FlowLayout — ничего не
            //    накладывается) + кнопка приватности справа ─────────────────
            _tabBar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(43, 45, 49) };

            var flow = new FlowLayoutPanel
            {
                Location = new Point(8, 10),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(43, 45, 49)
            };
            var title = new Label
            {
                Text = "👥 Друзья",
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(6, 7, 12, 0)
            };
            flow.Controls.Add(title);

            AddTab(flow, Tab.Online, "В сети");
            AddTab(flow, Tab.All, "Все");
            AddTab(flow, Tab.Pending, "Ожидание");
            AddTab(flow, Tab.Add, "Добавить");
            _tabBar.Controls.Add(flow);

            _btnPrivacy = new Button
            {
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Neutral,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Size = new Size(190, 30),
                Location = new Point(ClientSize.Width - 202, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            _btnPrivacy.FlatAppearance.BorderSize = 0;
            _btnPrivacy.Click += (s, e) => TogglePrivacyMenu();
            _tabBar.Controls.Add(_btnPrivacy);
            RefreshPrivacyButton();

            // ── Поиск (виден только на вкладке «Добавить») ────────────────
            _search = new TextBox
            {
                Dock = DockStyle.Top,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(30, 31, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f),
                PlaceholderText = "Логин (@имя / #имя) или имя",
                Visible = false
            };
            _search.TextChanged += (s, e) => { if (_tab == Tab.Add) Reload(); };

            _status = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                ForeColor = Muted,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0)
            };

            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Bg,
                Padding = new Padding(10, 6, 10, 10)
            };
            _list.Resize += (s, e) =>
            {
                int w = Math.Max(400, _list.ClientSize.Width - 26);
                foreach (Control c in _list.Controls) c.Width = w;
            };

            _content = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            _content.Controls.Add(_list);
            _content.Controls.Add(_status);
            _content.Controls.Add(_search);

            Controls.Add(_content);
            Controls.Add(_tabBar);

            SelectTab(Tab.Online);
        }

        private void AddTab(FlowLayoutPanel flow, Tab tab, string text)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Muted,
                BackColor = Color.FromArgb(43, 45, 49),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 4),
                Margin = new Padding(2, 2, 2, 0),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 58, 64);
            b.Click += (s, e) => SelectTab(tab);
            _tabButtons[tab] = b;
            flow.Controls.Add(b);
        }

        private void SelectTab(Tab tab)
        {
            _tab = tab;
            foreach (var kv in _tabButtons)
            {
                bool on = kv.Key == tab;
                kv.Value.ForeColor = on ? Color.White : Muted;
                kv.Value.BackColor = on ? Accent : Color.FromArgb(43, 45, 49);
            }
            _search.Visible = tab == Tab.Add;
            Reload();
        }

        // ── Приватность «кто может мне писать» ────────────────────────────
        private void RefreshPrivacyButton()
        {
            int mode = FriendsRepository.GetDmPrivacy(_me);
            _btnPrivacy.Text = mode == 1 ? "✉ Писать: Только друзья ▾" : "✉ Писать: Все ▾";
        }

        private void TogglePrivacyMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Card,
                ForeColor = Color.White,
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors())
            };
            int mode = FriendsRepository.GetDmPrivacy(_me);
            var all = new ToolStripMenuItem("✔ Все") { Checked = mode == 0, ForeColor = Color.White };
            var fr = new ToolStripMenuItem("🔒 Только друзья") { Checked = mode == 1, ForeColor = Color.White };
            all.Click += (s, e) => { FriendsRepository.SetDmPrivacy(_me, 0); RefreshPrivacyButton(); };
            fr.Click += (s, e) => { FriendsRepository.SetDmPrivacy(_me, 1); RefreshPrivacyButton(); };
            menu.Items.Add(all);
            menu.Items.Add(fr);
            menu.Show(_btnPrivacy, new Point(0, _btnPrivacy.Height));
        }

        /// <summary>Тёмная палитра для выпадающих меню (в стиле Discord).</summary>
        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Card;
            public override Color MenuItemSelected => Color.FromArgb(64, 68, 75);
            public override Color MenuItemBorder => Color.FromArgb(64, 68, 75);
            public override Color MenuBorder => Color.FromArgb(30, 31, 34);
            public override Color ImageMarginGradientBegin => Card;
            public override Color ImageMarginGradientMiddle => Card;
            public override Color ImageMarginGradientEnd => Card;
            public override Color CheckBackground => Accent;
            public override Color CheckSelectedBackground => Accent;
            public override Color CheckPressedBackground => Accent;
        }

        // ── Наполнение списка по вкладке ──────────────────────────────────
        private void Reload()
        {
            if (_tab == Tab.Add) { RunSearch(); return; }

            _list.Controls.Clear();
            _status.Text = "Загрузка…";

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    List<FriendsRepository.UserHit> rows;
                    HashSet<int> online = null;

                    if (_tab == Tab.Pending)
                    {
                        rows = new List<FriendsRepository.UserHit>();
                        rows.AddRange(FriendsRepository.IncomingRequests(_me));
                        rows.AddRange(FriendsRepository.OutgoingRequests(_me));
                    }
                    else
                    {
                        rows = FriendsRepository.Friends(_me);
                        var ids = new List<int>();
                        foreach (var f in rows) ids.Add(f.Id);
                        online = FriendsRepository.OnlineIds(ids);
                        if (_tab == Tab.Online)
                            rows = rows.FindAll(f => online.Contains(f.Id));
                    }

                    if (IsDisposed || !IsHandleCreated) return;
                    var fOnline = online;
                    var fRows = rows;
                    BeginInvoke(new Action(() =>
                    {
                        _list.Controls.Clear();
                        _status.Text = _tab switch
                        {
                            Tab.Online => $"В сети — {fRows.Count}",
                            Tab.All => $"Всего друзей — {fRows.Count}",
                            Tab.Pending => fRows.Count == 0 ? "Заявок нет" : $"Заявок — {fRows.Count}",
                            _ => ""
                        };
                        foreach (var h in fRows)
                            _list.Controls.Add(MakeRow(h, fOnline != null && fOnline.Contains(h.Id)));
                    }));
                }
                catch { }
            });
        }

        private void RunSearch()
        {
            string q = _search.Text;
            _list.Controls.Clear();
            if (string.IsNullOrWhiteSpace(q)) { _status.Text = "Введите логин или имя, чтобы найти пользователя."; return; }
            _status.Text = "Поиск…";
            System.Threading.Tasks.Task.Run(() =>
            {
                var hits = FriendsRepository.Search(_me, q);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_search.Text != q) return;
                        _list.Controls.Clear();
                        _status.Text = hits.Count == 0 ? "Никого не найдено." : $"Найдено: {hits.Count}";
                        foreach (var h in hits) _list.Controls.Add(MakeRow(h, false, showPresence: false));
                    }));
                }
                catch { }
            });
        }

        // ── Карточка пользователя ─────────────────────────────────────────
        private Control MakeRow(FriendsRepository.UserHit h, bool isOnline, bool showPresence = true)
        {
            int w = Math.Max(400, _list.ClientSize.Width - 26);
            var card = new Panel { Width = w, Height = 56, Margin = new Padding(0, 0, 0, 6), BackColor = Card };
            card.MouseEnter += (s, e) => card.BackColor = CardHover;
            card.MouseLeave += (s, e) => card.BackColor = Card;

            // Точка присутствия (только для друзей).
            if (showPresence)
            {
                var dot = new Panel { Size = new Size(12, 12), Location = new Point(14, 22), BackColor = Card };
                dot.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using var br = new SolidBrush(isOnline ? OnlineDot : OfflineDot);
                    e.Graphics.FillEllipse(br, 0, 0, 11, 11);
                };
                card.Controls.Add(dot);
            }
            int textX = showPresence ? 36 : 14;

            var lblName = new Label
            {
                Text = h.Name,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(w - textX - 230, 20),
                Location = new Point(textX, 9),
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            string sub = string.IsNullOrWhiteSpace(h.Login) ? "" : "@" + h.Login;
            if (h.Rel == FriendsRepository.Relation.IncomingPending) sub = "📨 хочет добавить вас  ·  " + sub;
            else if (h.Rel == FriendsRepository.Relation.OutgoingPending) sub = "⏳ заявка отправлена  ·  " + sub;
            else if (showPresence) sub = (isOnline ? "В сети" : "Не в сети") + (sub.Length > 0 ? "  ·  " + sub : "");
            var lblSub = new Label
            {
                Text = sub,
                ForeColor = Muted,
                AutoSize = false,
                Size = new Size(w - textX - 230, 18),
                Location = new Point(textX, 30),
                Font = new Font("Segoe UI", 8.5f),
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(lblName);
            card.Controls.Add(lblSub);

            // Кнопки справа зависят от отношения / вкладки.
            int bx = w - 12;
            void AddBtn(string text, Color bg, int width, Action onClick)
            {
                bx -= width;
                var b = new Button
                {
                    Text = text, Size = new Size(width, 30), Location = new Point(bx, 13),
                    FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold), Cursor = Cursors.Hand,
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => onClick();
                card.Controls.Add(b);
                b.BringToFront();
                bx -= 6;
            }

            // «Написать» без дружбы: разрешено, если у адресата НЕ включено
            // «только друзья» (админ обходит ограничение). Иначе — подсказка
            // сначала отправить заявку и дождаться принятия.
            void TryWrite()
            {
                bool isAdmin = string.Equals(UserSession.Role, "admin", StringComparison.OrdinalIgnoreCase);
                if (FriendsRepository.CanMessage(_me, h.Id, isAdmin))
                {
                    OpenChatWith = h.Id;
                    Changed = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(this,
                        "Этот пользователь принимает сообщения только от друзей.\n" +
                        "Отправьте заявку — когда её примут, вы сможете написать.",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            switch (h.Rel)
            {
                case FriendsRepository.Relation.Friend:
                    AddBtn("➖", Neutral, 40, () => { FriendsRepository.Remove(_me, h.Id); Changed = true; Reload(); });
                    AddBtn("✉ Написать", Green, 100, () => { OpenChatWith = h.Id; Changed = true; Close(); });
                    break;
                case FriendsRepository.Relation.IncomingPending:
                    AddBtn("✖ Отклонить", Neutral, 100, () => { FriendsRepository.DeclineRequest(_me, h.Id); Changed = true; Reload(); });
                    AddBtn("✔ Принять", Accent, 100, () => { FriendsRepository.AcceptRequest(_me, h.Id); Changed = true; Reload(); });
                    break;
                case FriendsRepository.Relation.OutgoingPending:
                    AddBtn("✉ Написать", Neutral, 100, TryWrite);
                    AddBtn("⏳ Отменить", Neutral, 110, () => { FriendsRepository.Remove(_me, h.Id); Changed = true; Reload(); });
                    break;
                default:
                    AddBtn("✉ Написать", Neutral, 100, TryWrite);
                    AddBtn("📨 Заявка", Green, 100, () =>
                    {
                        FriendsRepository.SendRequest(_me, h.Id);
                        h.Rel = FriendsRepository.Relation.OutgoingPending;
                        Changed = true; Reload();
                    });
                    break;
            }

            return card;
        }
    }
}
