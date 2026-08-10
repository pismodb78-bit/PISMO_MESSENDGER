using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Статусы присутствия участников сервера (в сети / бездействует / не в сети) —
    /// как в мессенджере. Рисуем цветную точку слева от имени участника.
    /// </summary>
    public sealed partial class ServersForm
    {
        // uid -> 0 не в сети, 1 бездействует, 2 в сети
        private readonly Dictionary<int, int> _memberPresence = new();
        // uid -> кнопка участника (для точечной перерисовки без пересборки списка)
        private readonly List<(int uid, Button btn)> _memberButtons = new();
        private bool _serverPresenceOk = true;

        /// <summary>Гасим присутствие ТОЛЬКО при реально отсутствующих колонках/таблице
        /// (1054/1146). Обрыв связи с БД временный: раньше он навсегда выключал
        /// присутствие, и участники висели «не в сети» до перезапуска.</summary>
        private static bool IsSchemaMissingSrv(Exception ex)
            => ex is MySqlException my && (my.Number == 1054 || my.Number == 1146);

        private static readonly Color SrvPresenceOnline = Color.FromArgb(59, 165, 93);
        private static readonly Color SrvPresenceIdle = Color.FromArgb(240, 178, 50);
        private static readonly Color SrvPresenceOffline = Color.FromArgb(116, 127, 141);

        /// <summary>Привязывает к кнопке участника аватар (слева) и цветную точку
        /// статуса бейджем в углу аватара — как в мессенджере/Discord. Вызывается
        /// из LoadMembers.</summary>
        private void AttachPresenceDot(Button b, int uid, string name)
        {
            const int AV = 26;                     // размер аватара
            int avX = 6, avY = (b.Height - AV) / 2;
            // Освобождаем слева место под аватар (аватар + отступ).
            b.Padding = new Padding(avX + AV + 8, 0, 0, 0);
            b.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // Аватар: картинка из кэша, иначе цветной кружок с инициалом.
                if (!AvatarStore.DrawAvatar(g, uid, avX, avY, AV))
                {
                    string full = name ?? "";
                    int h = 0; foreach (char ch in full) h = (h * 31 + ch) & 0x7fffffff;
                    Color[] pal = { Color.FromArgb(88,101,242), Color.FromArgb(235,69,158),
                        Color.FromArgb(59,165,93), Color.FromArgb(250,166,26), Color.FromArgb(0,176,244) };
                    using var abr = new SolidBrush(pal[h % pal.Length]);
                    g.FillEllipse(abr, avX, avY, AV, AV);
                    string letter = full.Length > 0 ? full.Substring(0, 1).ToUpper() : "?";
                    using var af = new Font("Segoe UI Black", 9f, FontStyle.Bold);
                    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(letter, af, Brushes.White, new RectangleF(avX, avY, AV, AV), sf);
                }

                // Точка статуса — бейджем в правом нижнем углу аватара, с «вырезом»
                // под цвет кнопки, чтобы читалась поверх картинки.
                if (_memberPresence.TryGetValue(uid, out int st))
                {
                    Color col = st switch { 2 => SrvPresenceOnline, 1 => SrvPresenceIdle, _ => SrvPresenceOffline };
                    int d = 9, ring = 3;
                    int bx = avX + AV - d, by = avY + AV - d;
                    using (var rbr = new SolidBrush(b.BackColor))
                        g.FillEllipse(rbr, bx - ring / 2, by - ring / 2, d + ring, d + ring);
                    using var br = new SolidBrush(col);
                    g.FillEllipse(br, bx, by, d, d);
                }
            };
            AvatarStore.EnsureLoaded(uid);
            _memberButtons.Add((uid, b));
        }

        /// <summary>Асинхронно читает присутствие участников и перерисовывает точки.</summary>
        private void RefreshMemberPresence()
        {
            if (!_serverPresenceOk) return;
            var ids = new List<int>();
            foreach (var (uid, _) in _memberButtons) ids.Add(uid);
            if (ids.Count == 0) return;

            _ = Task.Run(() =>
            {
                var fresh = ReadServerPresence(ids);
                try
                {
                    if (IsDisposed || !IsHandleCreated || fresh == null) return;
                    BeginInvoke(new Action(() =>
                    {
                        _memberPresence.Clear();
                        foreach (var kv in fresh) _memberPresence[kv.Key] = kv.Value;
                        foreach (var (_, b) in _memberButtons)
                            try { if (!b.IsDisposed) b.Invalidate(); } catch { }
                    }));
                }
                catch { }
            });
        }

        private Dictionary<int, int> ReadServerPresence(List<int> ids)
        {
            if (!_serverPresenceOk || ids == null || ids.Count == 0) return null;
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
                    int seenAgo = r["seen_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["seen_ago"]);
                    int activeAgo = r["active_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["active_ago"]);

                    int status;
                    if (seenAgo > 40) status = 0;        // не в сети
                    else if (activeAgo > 90) status = 1;  // бездействует
                    else status = 2;                      // в сети
                    result[id] = status;
                }
                return result;
            }
            catch (Exception ex) { if (IsSchemaMissingSrv(ex)) _serverPresenceOk = false; return null; }
        }
    }
}
