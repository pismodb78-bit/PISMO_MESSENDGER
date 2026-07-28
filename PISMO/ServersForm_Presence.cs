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

        private static readonly Color SrvPresenceOnline = Color.FromArgb(59, 165, 93);
        private static readonly Color SrvPresenceIdle = Color.FromArgb(240, 178, 50);
        private static readonly Color SrvPresenceOffline = Color.FromArgb(116, 127, 141);

        /// <summary>Привязывает к кнопке участника отрисовку цветной точки статуса
        /// в левой части (с отступом текста). Вызывается из LoadMembers.</summary>
        private void AttachPresenceDot(Button b, int uid)
        {
            // Сдвигаем текст вправо, чтобы освободить место под точку.
            b.Padding = new Padding(16, 0, 0, 0);
            b.Paint += (s, e) =>
            {
                if (!_memberPresence.TryGetValue(uid, out int st)) return;
                Color col = st switch { 2 => SrvPresenceOnline, 1 => SrvPresenceIdle, _ => SrvPresenceOffline };
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int d = 9;
                int x = 5, y = (b.Height - d) / 2;
                using var br = new SolidBrush(col);
                g.FillEllipse(br, x, y, d, d);
            };
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
            catch { _serverPresenceOk = false; return null; }
        }
    }
}
