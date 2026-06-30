using System;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

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

        private const int RailWidth = 72;
        private const int RailCircle = 48;

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
            // Док обрабатывается от ВЫСШЕГО индекса к низшему: чтобы рейл занял
            // самый левый край (левее сайдбара), его индекс должен быть наибольшим.
            try { Controls.SetChildIndex(_serverRail, Controls.Count - 1); } catch { }

            // Когда серверов много и появляется вертикальный скролл — подгоняем ширину
            // кружков под клиентскую область, чтобы не вылезал горизонтальный скролл.
            _serverRail.Resize += (s, e) => FitRailItems();

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

            // 1) «Личные сообщения» — домой.
            var home = MakeRailCircle("ЛС", Color.FromArgb(88, 101, 242), Color.White, isHome: true);
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
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT s.id, s.name FROM servers s " +
                    "JOIN server_members m ON m.server_id = s.id " +
                    "WHERE m.user_id=@me ORDER BY s.id", conn);
                cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int sid = Convert.ToInt32(r["id"]);
                    string name = r["name"].ToString();
                    var c = MakeRailCircle(ServerInitials(name), ServerColor(sid), Color.White);
                    c.Tag = sid;
                    c.Click += (s, e) => OpenServerFromRail(sid);
                    _tooltip.SetToolTip(c, name);
                    _serverRail.Controls.Add(c);
                }
            }
            catch { /* нет связи — рейл просто без серверов, домой работает */ }

            // 4) «+» — создать / войти на сервер.
            var add = MakeRailCircle("+", Color.FromArgb(45, 47, 51), Color.FromArgb(59, 165, 93));
            add.Click += (s, e) => OpenServerFromRail(-1);
            _tooltip.SetToolTip(add, "Добавить сервер");
            _serverRail.Controls.Add(add);

            _serverRail.ResumeLayout();
            FitRailItems();
        }

        private readonly ToolTip _tooltip = new ToolTip();

        /// <summary>Круглая «иконка» рейла (Panel с собственной отрисовкой).</summary>
        private Panel MakeRailCircle(string text, Color back, Color fore, bool isHome = false)
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
                int radius = (selected || hover) ? d : 16;

                using (var path = RoundedRect(rect, radius / 2))
                using (var br = new SolidBrush(back))
                    g.FillPath(br, path);

                using var fnt = new Font(text == "+" ? "Segoe UI" : "Segoe UI Semibold",
                                         text == "+" ? 20f : (text.Length > 2 ? 11f : 14f),
                                         FontStyle.Bold);
                TextRenderer.DrawText(g, text, fnt, rect, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                // Белый «пилюль»-индикатор слева (как в Discord) — для выбранного/наведённого.
                if (selected || hover)
                {
                    int ph = selected ? 28 : 16;
                    using var pb = new SolidBrush(Color.White);
                    using var pp = RoundedRect(new Rectangle(0, y + (d - ph) / 2, 8, ph), 4);
                    g.FillPath(pb, pp);
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

        /// <summary>Клик по «Личные сообщения» — на передний план MainForm.</summary>
        private void SelectRailHome()
        {
            _railSelectedServerId = -1;
            try { _serverRail?.Invalidate(true); foreach (Control c in _serverRail.Controls) c.Invalidate(); } catch { }
            try { Activate(); BringToFront(); } catch { }
        }

        /// <summary>Открыть сервер (sid&gt;0) или окно добавления (sid=-1) в окне серверов.</summary>
        private void OpenServerFromRail(int sid)
        {
            try
            {
                if (_serversForm == null || _serversForm.IsDisposed)
                {
                    _serversForm = sid > 0 ? new ServersForm(sid) : new ServersForm();
                    _serversForm.FormClosed += (a, b) => { _serversForm = null; SelectRailHome(); LoadServerRailItems(); };
                    _serversForm.Show(this);
                }
                else
                {
                    if (sid > 0) _serversForm.OpenServer(sid);
                    _serversForm.Activate();
                }

                if (sid > 0)
                {
                    _railSelectedServerId = sid;
                    foreach (Control c in _serverRail.Controls) c.Invalidate();
                }
            }
            catch { }
        }
    }
}
