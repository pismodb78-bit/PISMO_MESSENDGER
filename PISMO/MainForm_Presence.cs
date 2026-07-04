using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Статусы присутствия (в сети / бездействует / не в сети) и индикатор
    /// активного звонка ("Звонок идёт: …"). Вынесено в partial, чтобы не
    /// перегружать основной MainForm.
    /// </summary>
    public partial class MainForm
    {
        // uid -> 0 не в сети, 1 бездействует, 2 в сети
        private readonly Dictionary<int, int> _presence = new();
        private System.Windows.Forms.Timer _presenceTimer;
        private bool _presenceColumnsOk = true;

        // Баннер активного звонка под заголовком чата.
        private Panel _pnlCallBanner;
        private Label _lblCallBanner;
        private int _bannerCallId = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static int SystemIdleSeconds()
        {
            try
            {
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref lii)) return 0;
                long idleMs = unchecked((uint)Environment.TickCount - lii.dwTime);
                return (int)(idleMs / 1000);
            }
            catch { return 0; }
        }

        /// <summary>Запускает heartbeat присутствия и индикатор звонка.
        /// Вызывается из MainForm_Load.</summary>
        private void StartPresence()
        {
            // Баннер «Звонок идёт» под шапкой чата.
            _pnlCallBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = Color.FromArgb(59, 165, 93),
                Cursor = Cursors.Hand,
                Visible = false
            };
            _lblCallBanner = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = ""
            };
            _pnlCallBanner.Controls.Add(_lblCallBanner);
            void JoinFromBanner(object s, EventArgs e)
            {
                // Подключиться к идущему звонку (StartCall сам зайдёт в существующую сессию).
                try { StartCall(false); } catch { }
            }
            _pnlCallBanner.Click += JoinFromBanner;
            _lblCallBanner.Click += JoinFromBanner;

            try
            {
                pnlMain.Controls.Add(_pnlCallBanner);
                _pnlCallBanner.BringToFront();
                pnlChatHeader.BringToFront(); // шапка остаётся выше баннера
            }
            catch { }

            // Сразу отметимся в сети и прочитаем статусы один раз.
            _ = Task.Run(() => WriteHeartbeat());
            PresenceTick();

            // Heartbeat пишем ЧАСТО (15с) — это критично: порог «не в сети» = 40с,
            // и если писать реже, статус «протухал» между ударами и точка мерцала
            // зелёный→серый. 15с << 40с даёт запас даже на пропущенный удар. Это один
            // лёгкий UPDATE (пул соединений), без обращения к UI — лага нет.
            // Чтение чужих статусов идёт в общем опросе (PollTick, 3с) с диффом.
            _presenceTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _presenceTimer.Tick += (s, e) =>
            {
                int idle = SystemIdleSeconds();
                _ = Task.Run(() => WriteHeartbeat(idle));
                // Дешёвая перерисовка своего кружка в футере: если аватар не успел
                // загрузиться к первому показу (или загрузка сорвалась), очередная
                // отрисовка подхватит его из кэша / повторит загрузку.
                try { pnlMyAvatar?.Invalidate(); } catch { }
            };
            _presenceTimer.Start();
        }

        private void PresenceTick()
        {
            // Собираем видимые id на UI-потоке.
            var ids = new List<int>();
            foreach (var p in _userPanels)
                if (p.Tag is int uid) ids.Add(uid);

            int idleSec = SystemIdleSeconds();

            _ = Task.Run(() =>
            {
                WriteHeartbeat(idleSec);
                var fresh = ReadPresence(ids);
                string bannerText; int bannerCall;
                BuildCallBanner(out bannerText, out bannerCall);

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(() =>
                    {
                        if (fresh != null)
                        {
                            _presence.Clear();
                            foreach (var kv in fresh) _presence[kv.Key] = kv.Value;
                            InvalidateCardAvatars();
                        }
                        ApplyCallBanner(bannerText, bannerCall);
                    });
                }
                catch { }
            });
        }

        private void WriteHeartbeat(int idleSec = 0)
        {
            if (!_presenceColumnsOk) return;
            try
            {
                int myId = UserSession.EffectiveId;
                bool active = idleSec < 60; // двигал мышь/печатал за последнюю минуту
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen=NOW(), last_active=IF(@act=1, NOW(), last_active) WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@act", active ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", myId);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Колонок ещё нет (миграция не выполнена) — тихо отключаем присутствие.
                _presenceColumnsOk = false;
            }
        }

        private Dictionary<int, int> ReadPresence(List<int> ids)
        {
            if (!_presenceColumnsOk || ids == null || ids.Count == 0) return null;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ids[i]);
                }
                string sql =
                    $"SELECT id, TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS seen_ago, " +
                    $"TIMESTAMPDIFF(SECOND, last_active, NOW()) AS active_ago " +
                    $"FROM users WHERE id IN ({sb})";

                var result = new Dictionary<int, int>();
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(sql, conn);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int id = Convert.ToInt32(r["id"]);
                    bool seenNull = r["seen_ago"] == DBNull.Value;
                    int seenAgo = seenNull ? int.MaxValue : Convert.ToInt32(r["seen_ago"]);
                    bool activeNull = r["active_ago"] == DBNull.Value;
                    int activeAgo = activeNull ? int.MaxValue : Convert.ToInt32(r["active_ago"]);

                    int status;
                    if (seenAgo > 40) status = 0;          // не в сети (heartbeat 15с, порог 40с)
                    else if (activeAgo > 90) status = 1;    // бездействует (нет ввода >1.5 мин)
                    else status = 2;                        // в сети
                    result[id] = status;
                }
                return result;
            }
            catch { _presenceColumnsOk = false; return null; }
        }

        private void InvalidateCardAvatars()
        {
            foreach (var p in _userPanels)
                foreach (Control c in p.Controls)
                    if (c is Panel avatar) { try { avatar.Invalidate(); } catch { } }
            // Свой кружок в футере тоже: если первая загрузка аватара сорвалась
            // (обрыв БД), перерисовка заставит DrawAvatar повторить попытку —
            // иначе внизу навсегда оставалась буква-заглушка.
            try { pnlMyAvatar?.Invalidate(); } catch { }
        }

        private static readonly Color PresenceOnline = Color.FromArgb(59, 165, 93);
        private static readonly Color PresenceIdle = Color.FromArgb(240, 178, 50);
        private static readonly Color PresenceOffline = Color.FromArgb(116, 127, 141);

        /// <summary>Рисует цветную точку статуса в правом нижнем углу аватара.
        /// Если статус для uid неизвестен (миграция не выполнена) — ничего не рисует.</summary>
        private void DrawPresenceDot(Graphics g, int avatarW, int avatarH, int uid)
        {
            if (!_presence.TryGetValue(uid, out int st)) return;
            Color col = st switch { 2 => PresenceOnline, 1 => PresenceIdle, _ => PresenceOffline };
            int d = 12;
            int x = avatarW - d, y = avatarH - d;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Обводка цветом фона карточки, чтобы точка читалась поверх аватара.
            using var border = new SolidBrush(Color.FromArgb(47, 49, 54));
            g.FillEllipse(border, x - 2, y - 2, d + 3, d + 3);
            using var br = new SolidBrush(col);
            g.FillEllipse(br, x, y, d, d);
        }

        // ── Индикатор активного звонка ───────────────────────────────────
        private void BuildCallBanner(out string text, out int callId)
        {
            text = null; callId = -1;
            try
            {
                int myId = UserSession.EffectiveId;
                using var conn = DBHelper.OpenConnection();

                int sid = -1;
                if (_currentGroupId >= 0)
                {
                    using var c = new MySqlCommand(
                        "SELECT id FROM call_sessions WHERE group_id=@g AND status IN ('ringing','active') ORDER BY id DESC LIMIT 1", conn);
                    c.Parameters.AddWithValue("@g", _currentGroupId);
                    var o = c.ExecuteScalar();
                    if (o != null && o != DBNull.Value) sid = Convert.ToInt32(o);
                }
                else if (_currentChatPartnerId >= 0)
                {
                    using var c = new MySqlCommand(
                        "SELECT id FROM call_sessions WHERE ((caller_id=@me AND callee_id=@p) OR (caller_id=@p AND callee_id=@me)) " +
                        "AND status IN ('ringing','active') ORDER BY id DESC LIMIT 1", conn);
                    c.Parameters.AddWithValue("@me", myId);
                    c.Parameters.AddWithValue("@p", _currentChatPartnerId);
                    var o = c.ExecuteScalar();
                    if (o != null && o != DBNull.Value) sid = Convert.ToInt32(o);
                }

                if (sid < 0) return;

                var names = new List<string>();
                using (var pc = new MySqlCommand(
                    "SELECT TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login FROM call_participants cp " +
                    "JOIN users u ON u.id = cp.user_id WHERE cp.call_id=@cid AND cp.left_at IS NULL ORDER BY cp.joined_at ASC", conn))
                {
                    pc.Parameters.AddWithValue("@cid", sid);
                    using var r = pc.ExecuteReader();
                    while (r.Read())
                    {
                        string nm = r["nm"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(nm)) nm = r["login"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(nm)) names.Add(nm);
                    }
                }

                callId = sid;
                if (names.Count > 0)
                    text = $"📞 Звонок идёт ({names.Count}): {string.Join(", ", names)} — нажмите, чтобы присоединиться";
                else
                    text = "📞 Звонок идёт — нажмите, чтобы присоединиться";
            }
            catch { text = null; callId = -1; }
        }

        private void ApplyCallBanner(string text, int callId)
        {
            if (_pnlCallBanner == null) return;
            _bannerCallId = callId;

            // Не показываем баннер, если мы уже в этом звонке (форма открыта).
            bool inCall = _activeCall != null && !_activeCall.IsDisposed;

            if (!string.IsNullOrEmpty(text) && !inCall)
            {
                _lblCallBanner.Text = text;
                _pnlCallBanner.Height = 30;
                _pnlCallBanner.Visible = true;
            }
            else
            {
                _pnlCallBanner.Visible = false;
                _pnlCallBanner.Height = 0;
            }
        }
    }
}
