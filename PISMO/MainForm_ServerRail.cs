using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    // Левый узкий рейл «Личные сообщения + серверы», как в Discord:
    //   ┌──┐
    //   │ЛС│  ← кнопка «Личные сообщения» (домой)
    //   │──│  ← разделитель
    //   │🟣│  ← иконки серверов (где я состою)
    //   │🟢│
    //   │ +│  ← создать / войти на сервер
    //   └──┘
    public partial class MainForm : Form
    {
        private FlowLayoutPanel _serverRail;
        private int _railSelectedServerId = -1;   // -1 = выбраны «Личные сообщения»
        private Image _homeIcon;                  // иконка PISMO для кнопки «домой» (если есть)
        private Panel _serverEmbedHost;           // контейнер встроенного окна серверов (как Discord)

        private const int RailWidth = 72;
        private const int RailCircle = 48;

        // Бейджи серверов: serverId → (непрочитанные, упоминания). Рисуются на иконке
        // рейла; красный кружок с числом = упоминания/ответы, синий = просто непрочит.
        private readonly System.Collections.Generic.Dictionary<int, (int unread, int mentions)> _serverBadges = new();
        private readonly System.Collections.Generic.Dictionary<int, int> _prevServerMentions = new();
        private readonly System.Collections.Generic.Dictionary<int, string> _railServerNames = new();
        private string _railMyLogin;
        private bool _railBadgesInit;   // первый проход — без пушей (только заполнить базу)
        // Есть ли в таблице servers колонки оформления (миграция 24). Если нет —
        // спрашиваем их один раз и дальше читаем список по-старому.
        private static bool _railLookColumns = true;

        /// <summary>Колонки оформления есть — их только что успешно записали.</summary>
        internal static void NoteServerLookColumnsExist() => _railLookColumns = true;

        /// <summary>Создаёт левый рейл и встраивает его левее списка диалогов.</summary>
        private void BuildServerRail()
        {
            _serverRail = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                Width = RailWidth,
                BackColor = Color.FromArgb(30, 31, 34),   // Discord #1e1f22
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 10, 0, 10)
            };

            Controls.Add(_serverRail);

            // Контейнер встроенного окна серверов (как в Discord — всё в одном окне):
            // Fill, поверх области ЛС; виден только когда открыт сервер.
            _serverEmbedHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(54, 57, 63),
                Visible = false
            };
            Controls.Add(_serverEmbedHost);

            // Z-порядок дока (обрабатывается от ВЫСШЕГО индекса к низшему):
            //   рейл (Left) — наибольший индекс (левый край),
            //   Fill-контейнеры (pnlMain / _serverEmbedHost) — наименьшие (заполняют остаток).
            try { Controls.SetChildIndex(_serverEmbedHost, 0); } catch { }
            try { Controls.SetChildIndex(_serverRail, Controls.Count - 1); } catch { }

            // Когда серверов много и появляется вертикальный скролл — подгоняем ширину
            // кружков под клиентскую область, чтобы не вылезал горизонтальный скролл.
            _serverRail.Resize += (s, e) => FitRailItems();

            // Иконка PISMO для кнопки «домой» (как логотип Discord). Если файла нет —
            // в кнопке покажется текст «ЛС».
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pismo.ico");
                if (File.Exists(p)) _homeIcon = new Icon(p, 44, 44).ToBitmap();
            }
            catch { _homeIcon = null; }

            LoadServerRailItems();
        }

        private bool _fittingRail;
        private void FitRailItems()
        {
            if (_serverRail == null || _fittingRail) return;
            _fittingRail = true;
            try
            {
                int w = _serverRail.ClientSize.Width;
                if (w < 8) return;
                foreach (Control c in _serverRail.Controls)
                    if (c.Height == RailCircle + 8) c.Width = w;   // кружки рисуются по центру pnl.Width
            }
            finally { _fittingRail = false; }
        }

        /// <summary>Перестраивает содержимое рейла (домой + серверы + «+»).</summary>
        public void LoadServerRailItems()
        {
            if (_serverRail == null) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(LoadServerRailItems)); } catch { } return; }

            _serverRail.SuspendLayout();
            _serverRail.Controls.Clear();

            // 1) «Личные сообщения» — домой (иконка PISMO либо текст «ЛС»).
            var home = MakeRailCircle(_homeIcon != null ? "" : "ЛС",
                Color.FromArgb(88, 101, 242), Color.White, isHome: true, icon: _homeIcon);
            home.Click += (s, e) => SelectRailHome();
            _tooltip.SetToolTip(home, "Личные сообщения");
            _serverRail.Controls.Add(home);

            // 2) Разделитель.
            _serverRail.Controls.Add(new Panel
            {
                Width = RailWidth - 20,
                Height = 2,
                Margin = new Padding(10, 6, 10, 8),
                BackColor = Color.FromArgb(60, 63, 68)
            });

            // 3) Серверы, где я состою.
            var needIcons = new System.Collections.Generic.List<int>();
            try
            {
                var dt = new DataTable();

                // Без связи рельс берём из сохранённого: иначе серверы
                // исчезают с экрана целиком, хотя их каналы и переписки лежат
                // на диске и открылись бы.
                if (ConnectionGuard.Offline)
                {
                    dt = OfflineStore.Load(OfflineStore.ServersKey) ?? new DataTable();
                }
                else
                {
                using var conn = DBHelper.OpenConnection();
                // Саму картинку не тянем: в рейле нужен только факт её наличия,
                // а блоб за каждым сервером при каждой пересборке — это мегабайты
                // по сети на ровном месте.
                if (_railLookColumns)
                {
                    try
                    {
                        using var cmd = new MySqlCommand(
                            "SELECT s.id, s.name, COALESCE(s.avatar_color,'') AS avatar_color, " +
                            "(s.avatar_data IS NOT NULL) AS has_avatar FROM servers s " +
                            "JOIN server_members m ON m.server_id = s.id " +
                            "WHERE m.user_id=@me ORDER BY s.id", conn);
                        cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                        new MySqlDataAdapter(cmd).Fill(dt);
                    }
                    catch (MySqlException mex) when (mex.Number == 1054)
                    {
                        // Миграция 24 ещё не прошла — значки будут буквенными,
                        // но список серверов в рейле обязан остаться.
                        _railLookColumns = false;
                        dt = new DataTable();
                    }
                }
                if (!_railLookColumns)
                {
                    using var cmd = new MySqlCommand(
                        "SELECT s.id, s.name FROM servers s " +
                        "JOIN server_members m ON m.server_id = s.id " +
                        "WHERE m.user_id=@me ORDER BY s.id", conn);
                    cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                    new MySqlDataAdapter(cmd).Fill(dt);
                }
                OfflineStore.Save(OfflineStore.ServersKey, dt);
                OfflinePrefetch.ScheduleServers(dt);
                }

                bool hasLook = dt.Columns.Contains("avatar_color");
                foreach (DataRow r in dt.Rows)
                {
                    int sid = Convert.ToInt32(r["id"]);
                    string name = r["name"].ToString();
                    _railServerNames[sid] = name;

                    string colorHex = hasLook ? (r["avatar_color"]?.ToString() ?? "") : "";
                    bool hasAvatar = hasLook && r["has_avatar"] != DBNull.Value
                                     && Convert.ToInt32(r["has_avatar"]) != 0;

                    Image? icon = null;
                    if (hasAvatar)
                    {
                        // Пока картинка не прочитана — рисуем буквы, чтобы не
                        // держать поток интерфейса на запросе к базе.
                        icon = ServerLook.Cached(sid);
                        if (!ServerLook.Fresh(sid)) needIcons.Add(sid);
                    }
                    else ServerLook.Remember(sid, null);

                    // Под картинкой — выбранный цвет сервера, а если его нет,
                    // фон самого рейла: картинка с прозрачностью должна сидеть
                    // на фоне приложения, а не на цветном пятне. Буквенному
                    // значку цвет по id по-прежнему нужен — иначе серверы в
                    // рейле не отличить друг от друга.
                    var back = ServerLook.ParseColor(colorHex)
                               ?? (hasAvatar
                                   ? (_serverRail?.BackColor ?? Color.FromArgb(30, 31, 34))
                                   : ServerColor(sid));
                    var c = MakeRailCircle(ServerInitials(name), back, Color.White, icon: icon);
                    c.Tag = sid;
                    // ТОЛЬКО левый клик заходит на сервер. Раньше был Click (срабатывал
                    // и на ПКМ) → правый клик и меню открывал, и заходил в каналы.
                    c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenServerFromRail(sid); };
                    // ПКМ: «прочитать всё» на сервере — без захода в каналы.
                    int capSid = sid; string capName = name;
                    c.MouseUp += (s, e) =>
                    {
                        if (e.Button != MouseButtons.Right) return;
                        var menu = new ContextMenuStrip
                        {
                            BackColor = Color.FromArgb(24, 25, 28),
                            ForeColor = Color.FromArgb(220, 221, 222)
                        };
                        menu.Items.Add($"✓✓  Прочитать все на «{capName}»", null, (s2, e2) =>
                            System.Threading.Tasks.Task.Run(() =>
                                ServerReads.MarkServerRead(UserSession.EffectiveId, capSid)));
                        // Выйти с сервера можно было только на телефоне.
                        menu.Items.Add(new ToolStripSeparator());
                        menu.Items.Add("🚪  Покинуть сервер", null, (s2, e2) => LeaveServer(capSid, capName));
                        menu.Show(Cursor.Position);
                    };
                    _tooltip.SetToolTip(c, name);
                    _serverRail.Controls.Add(c);
                }
            }
            catch { /* нет связи — рейл просто без серверов, домой работает */ }

            // 4) «+» — создать / войти на сервер (зеленеет при наведении, как в Discord).
            var add = MakeRailCircle("+", Color.FromArgb(45, 47, 51), Color.FromArgb(59, 165, 93), isAdd: true);
            add.Click += (s, e) => OpenServerFromRail(-1);
            _tooltip.SetToolTip(add, "Добавить сервер");
            _serverRail.Controls.Add(add);

            _serverRail.ResumeLayout();
            FitRailItems();

            // Картинки значков читаем в фоне и пересобираем рейл один раз —
            // когда все они прочитаны. Повтора не будет: Load помечает запись
            // прочитанной даже когда картинки нет.
            if (needIcons.Count > 0)
            {
                var ids = needIcons.ToArray();
                System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (int sid in ids) ServerLook.Load(sid);
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(LoadServerRailItems));
                    }
                    catch { }
                });
            }
        }

        private readonly ToolTip _tooltip = new ToolTip();

        /// <summary>Круглая «иконка» рейла (Panel с собственной отрисовкой).</summary>
        /// <summary>Пересчитывает бейджи серверов (непрочит./упоминания) в фоне и
        /// шлёт пуш при новых упоминаниях/ответах. Вызывается из PollTick.</summary>
        internal void RefreshServerBadges()
        {
            if (_serverRail == null) return;
            int me = UserSession.EffectiveId;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_railMyLogin == null)
                    {
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            using var cmd = new MySqlCommand("SELECT login FROM users WHERE id=@id", conn);
                            cmd.Parameters.AddWithValue("@id", me);
                            _railMyLogin = cmd.ExecuteScalar()?.ToString() ?? "";
                        }
                        catch { _railMyLogin = ""; }
                    }
                    // Заодно сверяем сам рейл с базой: состав серверов, их
                    // названия, цвета и наличие картинок. Событие по сокету
                    // могло не дойти — скажем, его послали, пока мы были без
                    // связи, — и тогда это единственный способ узнать, что нас
                    // забанили или что у сервера новый значок.
                    try { CheckRailSignature(); } catch { }

                    var badges = ServerReads.GetBadges(me, _railMyLogin);
                    var agg = new System.Collections.Generic.Dictionary<int, (int unread, int mentions)>();
                    var muted = new System.Collections.Generic.HashSet<int>();
                    foreach (var b in badges)
                    {
                        agg.TryGetValue(b.ServerId, out var cur);
                        agg[b.ServerId] = (cur.unread + b.Unread, cur.mentions + b.Mentions);
                        if (b.Muted) muted.Add(b.ServerId);
                    }
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        // Пуш при приросте упоминаний. НЕ шлём, если сервер заглушён
                        // ЭТИМ пользователем (muted_notifs по его user_id — на других не
                        // влияет) или сервер открыт прямо сейчас.
                        if (_railBadgesInit)
                        {
                            foreach (var kv in agg)
                            {
                                _prevServerMentions.TryGetValue(kv.Key, out int prev);
                                if (kv.Value.mentions > prev && kv.Key != _railSelectedServerId
                                    && !muted.Contains(kv.Key))
                                {
                                    string nm = _railServerNames.TryGetValue(kv.Key, out var n) ? n : "сервер";
                                    PushNotify("PISMO — упоминание",
                                        $"Новые упоминания/ответы на сервере «{nm}»");
                                }
                            }
                        }
                        _serverBadges.Clear();
                        _prevServerMentions.Clear();
                        foreach (var kv in agg) { _serverBadges[kv.Key] = kv.Value; _prevServerMentions[kv.Key] = kv.Value.mentions; }
                        _railBadgesInit = true;
                        try { foreach (Control c in _serverRail.Controls) c.Invalidate(); } catch { }
                    }));
                }
                catch { }
            });
        }

        private DateTime _railSigAt = DateTime.MinValue;
        private string _railSig;

        /// <summary>
        /// Сверяет рейл с базой и пересобирает его при расхождении.
        /// </summary>
        /// <remarks>
        /// Зовётся из фонового потока вместе с бейджами, но работает не чаще
        /// раза в полминуты: это лишний запрос к удалённой базе, а меняется
        /// рейл редко.
        ///
        /// Картинку в подпись не берём — её пришлось бы читать целиком. Зато
        /// пересобираем рейл и тогда, когда у какого-то значка истёк срок
        /// годности в памяти: пересборка сама перечитает именно такие.
        /// </remarks>
        private void CheckRailSignature()
        {
            if ((DateTime.UtcNow - _railSigAt).TotalSeconds < 30) return;
            _railSigAt = DateTime.UtcNow;

            var sb = new System.Text.StringBuilder();
            var ids = new System.Collections.Generic.List<int>();
            using (var conn = DBHelper.OpenConnection())
            {
                string sql = _railLookColumns
                    ? "SELECT s.id, s.name, COALESCE(s.avatar_color,'') AS c, " +
                      "(s.avatar_data IS NOT NULL) AS a FROM servers s " +
                      "JOIN server_members m ON m.server_id = s.id " +
                      "WHERE m.user_id=@me ORDER BY s.id"
                    : "SELECT s.id, s.name FROM servers s " +
                      "JOIN server_members m ON m.server_id = s.id " +
                      "WHERE m.user_id=@me ORDER BY s.id";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int id = Convert.ToInt32(r["id"]);
                    ids.Add(id);
                    sb.Append(id).Append('|').Append(r["name"]);
                    if (_railLookColumns)
                        sb.Append('|').Append(r["c"]).Append('|').Append(r["a"]);
                    sb.Append(';');
                }
            }

            string sig = sb.ToString();
            bool changed = sig != _railSig;
            _railSig = sig;
            if (!changed && !ServerLook.AnyStale(ids)) return;

            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(LoadServerRailItems));
            }
            catch { }
        }

        private Panel MakeRailCircle(string text, Color back, Color fore,
            bool isHome = false, bool isAdd = false, Image icon = null)
        {
            var pnl = new Panel
            {
                Width = RailWidth,
                Height = RailCircle + 8,
                Margin = new Padding(0, 0, 0, 8),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            bool hover = false;
            pnl.MouseEnter += (s, e) => { hover = true; pnl.Invalidate(); };
            pnl.MouseLeave += (s, e) => { hover = false; pnl.Invalidate(); };
            pnl.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                int d = RailCircle;
                int x = (pnl.Width - d) / 2;
                int y = 4;
                var rect = new Rectangle(x, y, d, d);

                // При наведении/выборе скруглённый квадрат «расплывается» в круг — здесь
                // фиксированный радиус: обычное состояние = squircle, выбранное = круг.
                bool selected = (isHome && _railSelectedServerId == -1) ||
                                (pnl.Tag is int tg && tg == _railSelectedServerId);
                // Серверы остаются плитками со скруглёнными углами; при наведении
                // и выборе форма становится мягче, но не схлопывается в кружок.
                int radius = selected ? 22 : hover ? 30 : 18;

                // Кнопка «+» при наведении зеленеет целиком (как в Discord).
                // Нейтральный фон кружка — через Theme.Map (кастомный Paint не
                // проходит авто-перекраску, в светлой теме оставался чёрным).
                Color drawBack = (isAdd && hover) ? Color.FromArgb(59, 165, 93) : Theme.Map(back);
                Color drawFore = (isAdd && hover) ? Color.White : fore;

                using (var path = RoundedRect(rect, radius / 2))
                {
                    if (selected || hover)
                    {
                        var shadowRect = rect; shadowRect.Offset(0, 2);
                        using var shadowPath = RoundedRect(shadowRect, radius / 2);
                        using var shadow = new SolidBrush(Color.FromArgb(selected ? 58 : 34, Color.Black));
                        g.FillPath(shadow, shadowPath);
                    }
                    using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                        rect, ControlPaint.Light(drawBack, 0.10f), drawBack, 45f))
                        g.FillPath(br, path);

                    if (selected)
                    {
                        using var outline = new Pen(Color.FromArgb(92, Color.White), 1f);
                        g.DrawPath(outline, path);
                    }

                    // Иконка обрезана по форме плитки.                    if (icon != null)
                    {
                        var st = g.Save();
                        g.SetClip(path);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(icon, rect);
                        g.Restore(st);
                    }
                }

                if (isAdd)
                {
                    // Плюс рисуем вручную двумя полосками — ровно по центру кружка
                    // (глиф «+» из шрифта оптически смещён и выглядит криво).
                    int cx = rect.X + d / 2, cy = rect.Y + d / 2;
                    const int arm = 20, th = 4;
                    using var pb = new SolidBrush(drawFore);
                    g.FillRectangle(pb, cx - arm / 2, cy - th / 2, arm, th);   // горизонтальная
                    g.FillRectangle(pb, cx - th / 2, cy - arm / 2, th, arm);   // вертикальная
                }
                else if (icon == null)
                {
                    using var fnt = new Font("Segoe UI Semibold", text.Length > 2 ? 11f : 14f, FontStyle.Bold);
                    TextRenderer.DrawText(g, text, fnt, rect, drawFore,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                // Белый «пилюль»-индикатор слева (как в Discord) — для выбранного/наведённого.
                // У кнопки «+» пилюли нет (в Discord её там не показывают — иначе криво).
                if ((selected || hover) && !isAdd)
                {
                    int ph = selected ? 28 : 16;
                    using var pb = new SolidBrush(Color.White);
                    using var pp = RoundedRect(new Rectangle(0, y + (d - ph) / 2, 8, ph), 4);
                    g.FillPath(pb, pp);
                }

                // Бейдж непрочитанных/упоминаний в правом нижнем углу иконки сервера.
                if (!isAdd && pnl.Tag is int sidBadge && sidBadge > 0
                    && _serverBadges.TryGetValue(sidBadge, out var bdg) && (bdg.unread > 0 || bdg.mentions > 0))
                {
                    bool men = bdg.mentions > 0;
                    int count = men ? bdg.mentions : bdg.unread;
                    string btxt = count > 99 ? "99+" : count.ToString();
                    using var bf = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    Size ts = TextRenderer.MeasureText(g, btxt, bf);
                    int bh = 18, bw = Math.Max(bh, ts.Width + 10);
                    int bx = rect.Right - bw + 6, by = rect.Bottom - bh + 4;
                    var brect = new Rectangle(bx, by, bw, bh);
                    // Обводка цветом фона рейла — чтобы бейдж «отрезался» от иконки.
                    using (var ring = new SolidBrush(_serverRail?.BackColor ?? Color.FromArgb(30, 31, 34)))
                    using (var rp = RoundedRect(Rectangle.Inflate(brect, 3, 3), (bh + 6) / 2))
                        g.FillPath(ring, rp);
                    using (var bb = new SolidBrush(men ? Color.FromArgb(237, 66, 69) : Color.FromArgb(88, 101, 242)))
                    using (var bp = RoundedRect(brect, bh / 2))
                        g.FillPath(bb, bp);
                    TextRenderer.DrawText(g, btxt, bf, brect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };
            return pnl;
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int dia = Math.Max(1, radius * 2);
            var path = new GraphicsPath();
            if (dia >= r.Width && dia >= r.Height) { path.AddEllipse(r); path.CloseFigure(); return path; }
            path.AddArc(r.X, r.Y, dia, dia, 180, 90);
            path.AddArc(r.Right - dia, r.Y, dia, dia, 270, 90);
            path.AddArc(r.Right - dia, r.Bottom - dia, dia, dia, 0, 90);
            path.AddArc(r.X, r.Bottom - dia, dia, dia, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Инициалы сервера (1–2 буквы) для иконки.</summary>
        private static string ServerInitials(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "?";
            var parts = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return ("" + parts[0][0] + parts[1][0]).ToUpperInvariant();
            return (name.Length >= 2 ? name.Substring(0, 2) : name).ToUpperInvariant();
        }

        /// <summary>Стабильный цвет иконки по id сервера (палитра в духе Discord).</summary>
        private static Color ServerColor(int sid)
        {
            Color[] palette =
            {
                Color.FromArgb(88, 101, 242),  // blurple
                Color.FromArgb(59, 165, 93),   // green
                Color.FromArgb(235, 69, 158),  // pink
                Color.FromArgb(250, 166, 26),  // orange
                Color.FromArgb(237, 66, 69),   // red
                Color.FromArgb(0, 168, 252),   // blue
                Color.FromArgb(155, 89, 182),  // purple
            };
            return palette[((sid % palette.Length) + palette.Length) % palette.Length];
        }

        /// <summary>Клик по «Личные сообщения» — показываем ЛС (прячем встроенный сервер).</summary>
        private void SelectRailHome()
        {
            _railSelectedServerId = -1;
            try { if (_serverEmbedHost != null) _serverEmbedHost.Visible = false; } catch { }
            try { pnlSidebar.Visible = true; pnlMain.Visible = true; } catch { }
            // Возвращаем голосовой док обратно в сайдбар ЛС.
            try { RestoreVoiceDockToSidebar(); } catch { }
            try { foreach (Control c in _serverRail.Controls) c.Invalidate(); } catch { }
        }

        /// <summary>Создаёт (один раз) встроенное окно серверов внутри MainForm.</summary>
        private void EnsureServerEmbed()
        {
            if (_serversForm == null || _serversForm.IsDisposed)
            {
                _serversForm = new ServersForm();
                _serversForm.EnterEmbeddedMode();      // без рамки, Dock=Fill, скрыть колонку серверов
                _serverEmbedHost.Controls.Add(_serversForm);
                _serversForm.Show();                   // для TopLevel=false — это просто показ внутри контейнера
            }
        }

        /// <summary>Показать встроенный сервер (sid&gt;0) или диалог добавления (sid=-1).</summary>
        private void OpenServerFromRail(int sid)
        {
            try
            {
                EnsureServerEmbed();

                if (sid > 0)
                {
                    _serversForm.OpenServer(sid);
                    _railSelectedServerId = sid;
                    pnlSidebar.Visible = false;
                    pnlMain.Visible = false;
                    _serverEmbedHost.Visible = true;
                    _serverEmbedHost.BringToFront();
                    foreach (Control c in _serverRail.Controls) c.Invalidate();
                    // Тот же голосовой док показываем в колонке каналов сервера
                    // (сайдбар ЛС с ним скрыт) — 1-в-1, без копий.
                    try { _serversForm.MountDock(_voiceDock); } catch { }
                }
                else
                {
                    // «+» — создать/войти (колонка серверов скрыта, поэтому через диалог),
                    // затем обновляем рейл, чтобы новый сервер появился иконкой.
                    _serversForm.AddServerDialog();
                    LoadServerRailItems();
                }
            }
            catch { }
        }

        /// <summary>
        /// Покинуть сервер — как на телефоне: участника убираем из сервера,
        /// остальным сообщаем, что состав сменился. Владельцу выходить нельзя
        /// — сервер остался бы без хозяина.
        /// </summary>
        private void LeaveServer(int serverId, string name)
        {
            try
            {
                int owner = 0;
                using (var conn = DBHelper.OpenConnection())
                using (var cmd = new MySqlCommand("SELECT owner_id FROM servers WHERE id=@s", conn))
                {
                    cmd.Parameters.AddWithValue("@s", serverId);
                    var o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value) owner = Convert.ToInt32(o);
                }
                if (owner == UserSession.EffectiveId)
                {
                    MessageBox.Show(this,
                        "Вы владелец этого сервера — его нельзя покинуть. " +
                        "Удалить сервер можно в его настройках.",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (MessageBox.Show(this,
                        $"Покинуть сервер «{name}»?\n\nВернуться можно будет по его ID, если вас не забанят.",
                        "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                using (var conn = DBHelper.OpenConnection())
                using (var cmd = new MySqlCommand(
                           "DELETE FROM server_members WHERE server_id=@s AND user_id=@u", conn))
                {
                    cmd.Parameters.AddWithValue("@s", serverId);
                    cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                    cmd.ExecuteNonQuery();
                }
                try { WebSocketSignalingClient.Instance.SendMessage("server_members", 0, serverId, "members"); }
                catch { }

                LoadServerRailItems();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось покинуть сервер:\n" + ex.Message, "PISMO");
            }
        }
    }
}
