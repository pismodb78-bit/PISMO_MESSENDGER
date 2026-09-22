using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MySqlConnector;

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

        /// <summary>Сколько секунд без heartbeat считаем «не в сети».
        /// Сам heartbeat идёт раз в 6 секунд, запас на промах и задержку.</summary>
        private const int SeenOfflineSec = 40;

        /// <summary>Сколько секунд без ввода считаем «бездействует».</summary>
        private const int ActiveIdleSec = 90;

        /// <summary>Статус по двум «сколько секунд назад». Один расчёт на всех,
        /// чтобы кружок в списке, подпись в шапке и то, что мы рассылаем по
        /// сокету, не разошлись между собой.</summary>
        private static int StatusFrom(int seenAgo, int activeAgo)
        {
            if (seenAgo > SeenOfflineSec) return 0;
            if (activeAgo > ActiveIdleSec) return 1;
            return 2;
        }

        // Свой статус, разосланный последним, и когда это было. Рассылаем по
        // изменению — иначе каждые 6 секунд каждый клиент писал бы всем
        // остальным одно и то же.
        private int _myBroadcastStatus = -1;
        private DateTime _myBroadcastAt = DateTime.MinValue;

        /// <summary>Когда по сокету последний раз приходил статус этого
        /// человека. По этой отметке снимок из базы не затирает то, что
        /// пришло секунду назад.</summary>
        private readonly Dictionary<int, DateTime> _presencePushedAt = new();
        private System.Windows.Forms.Timer _presenceTimer;
        private bool _presenceColumnsOk = true;

        /// <summary>Присутствие можно отключать ТОЛЬКО когда в схеме реально нет
        /// колонок/таблицы (1054 Unknown column, 1146 Table doesn't exist). Раньше в
        /// тот же catch попадал обрыв связи с БД, флаг гасился навсегда, и после
        /// переподключения человек висел «офлайн», продолжая писать сообщения.</summary>
        private static bool IsSchemaMissing(Exception ex)
            => ex is MySqlException my && (my.Number == 1054 || my.Number == 1146);

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
                // ВАЖНО: Fill-панель сообщений возвращаем в начало z-order, иначе она
                // докается РАНЬШЕ шапки, забирает всю высоту, и её полоса прокрутки
                // уходит под верхнюю панель к кнопке звонка.
                pnlMessages.BringToFront();
            }
            catch { }

            // Сразу отметимся в сети и прочитаем статусы один раз.
            _ = Task.Run(() => WriteHeartbeat());
            PresenceTick();
            try { RefreshServerBadges(); } catch { }   // бейджи серверов сразу при входе

            // Каждые 6с: heartbeat + чтение чужих статусов + перерисовка точек/шапки.
            // 6с << 40с (порог «не в сети») — статус не протухает. Частота выбрана в
            // тон серверному списку (там 2с), чтобы «в сети/бездействует» в мессенджере
            // и на сервере обновлялись примерно одинаково, без рассинхрона по времени.
            _presenceTimer = new System.Windows.Forms.Timer { Interval = 6000 };
            _presenceTimer.Tick += (s, e) =>
            {
                // PresenceTick = heartbeat + чтение ЧУЖИХ статусов + перерисовка точек
                // в списке (как ручное «Обновить»). Раньше по таймеру писался только
                // heartbeat, а точки в списке освежались лишь в PollTick, который при
                // живом WS выходит рано — отсюда рассинхрон: в шапке «бездействует», а
                // кружок в списке ещё зелёный до ручного обновления.
                PresenceTick();
                try { UpdateChatHeaderPresence(); } catch { }   // статус собеседника в шапке — та же частота
                try { RefreshServerBadges(); } catch { }        // бейджи/пуши упоминаний на серверах
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

            AnnouncePresence(idleSec);

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
                        // Тот же путь, что и у опроса в PollTick: и защита
                        // свежего прихода по сокету, и перерисовка только при
                        // изменении — в одном месте, а не в двух похожих.
                        ApplyPresence(fresh);
                        ApplyCallBanner(bannerText, bannerCall);
                    });
                }
                catch { }
            });
        }

        /// <summary>
        /// Рассылает СВОЙ статус по сокету, когда он изменился.
        ///
        /// Зачем, если есть heartbeat в базе. Оттуда статус доходит двумя
        /// шагами: сначала я должен записать (до 6 секунд), потом собеседник
        /// должен прочитать (ещё до 6 секунд). Каждый шаг — запрос к базе на
        /// другом конце сети, и любой из них может не успеть или не дойти;
        /// тогда «в сети» появлялось только со следующей сверкой. По сокету
        /// то же изменение приходит сразу же и всем.
        ///
        /// База остаётся источником правды: тот, кто подключился позже, и тот,
        /// до кого сообщение не дошло, всё равно всё узнают — просто через
        /// секунды, а не мгновенно.
        ///
        /// Шлём по изменению плюс раз в 30 секунд. Без этого каждый клиент
        /// каждые 6 секунд писал бы всем остальным одно и то же.
        /// </summary>
        private void AnnouncePresence(int idleSec)
        {
            try
            {
                // Сокета нет — и отмечать нечего: иначе мы бы «запомнили»
                // разосланный статус, которого никто не получил, и следующая
                // рассылка ушла бы только через полминуты после подключения.
                if (!WebSocketSignalingClient.Instance.IsConnected) return;

                int st = idleSec > ActiveIdleSec ? 1 : 2;
                bool changed = st != _myBroadcastStatus;
                bool stale = (DateTime.UtcNow - _myBroadcastAt).TotalSeconds >= 30;
                if (!changed && !stale) return;

                _myBroadcastStatus = st;
                _myBroadcastAt = DateTime.UtcNow;
                // sessionId — статус, payload — реальный простой в секундах:
                // из него получатель сразу строит «бездействует 5 мин», не
                // дожидаясь ответа базы.
                WebSocketSignalingClient.Instance.SendMessage(
                    "presence", 0, st, idleSec.ToString());
            }
            catch { }
        }

        /// <summary>
        /// Пришёл чужой статус по сокету. Применяем немедленно: кружок в
        /// списке и подпись в шапке, если это открытый собеседник.
        /// </summary>
        public void ApplyPresencePush(int senderId, int status, string payload)
        {
            if (senderId <= 0) return;
            if (status < 0 || status > 2) return;

            // Перерисовываем, только если статус ДЕЙСТВИТЕЛЬНО изменился и
            // этот человек вообще есть в списке. Статусы рассылаются всем
            // подряд, а карточки перерисовывать из-за незнакомца, которого
            // на экране нет, незачем.
            bool shown = false;
            foreach (var pnl in _userPanels)
                if (pnl.Tag is int u && u == senderId) { shown = true; break; }
            bool differs = !_presence.TryGetValue(senderId, out int prev) || prev != status;

            _presence[senderId] = status;
            _presencePushedAt[senderId] = DateTime.UtcNow;
            if (shown && differs) InvalidateCardAvatars();

            // _searchRowOpen — то же условие, что и в UpdateChatHeaderPresence:
            // строка поиска делит место в шапке с подписью статуса, и в
            // оконном режиме они накладываются друг на друга. Без этой
            // проверки приход по сокету возвращал подпись поверх поиска.
            if (senderId == _currentChatPartnerId && !TypingActive && !_searchRowOpen)
            {
                int idle = 0;
                int.TryParse(payload, out idle);
                if (idle < 0) idle = 0;
                try
                {
                    EnsureChatPresenceLabel();
                    var (text, color) = PresenceText(status, 0, idle);
                    _lblChatPresence.Text = text;
                    _lblChatPresence.ForeColor = color;
                    _presenceLabelPeer = senderId;
                    PositionChatPresence();
                    _lblChatPresence.Visible = true;
                    _lblChatPresence.BringToFront();
                }
                catch { }
            }
        }

        private void WriteHeartbeat(int idleSec = 0)
        {
            if (!_presenceColumnsOk) return;
            try
            {
                int myId = UserSession.EffectiveId;
                using var conn = DBHelper.OpenConnection();
                // Пишем НАСТОЯЩИЙ момент последнего ввода, а не «была ли
                // активность за минуту».
                //
                // Раньше было last_active = IF(простой < 60, NOW(), как было).
                // Из-за этого метка ещё целую минуту после последнего движения
                // мышью подтягивалась к текущему времени, и порог «90 секунд без
                // ввода» срабатывал не через 90 секунд, а через 150. Человек
                // отошёл от компьютера — собеседник видел «в сети» ещё две с
                // половиной минуты.
                //
                // GREATEST — чтобы метка не поехала назад: простой растёт, но
                // NOW() - простой стоит на месте, а после первого же нажатия
                // клавиши уходит вперёд.
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen=NOW(), " +
                    "last_active = GREATEST(COALESCE(last_active, '1970-01-02'), " +
                    "                       NOW() - INTERVAL @idle SECOND) " +
                    "WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@idle", idleSec < 0 ? 0 : idleSec);
                cmd.Parameters.AddWithValue("@id", myId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Отключаем присутствие только если колонок нет в схеме; обрыв связи —
                // временный, после переподключения heartbeat должен продолжиться.
                if (IsSchemaMissing(ex)) _presenceColumnsOk = false;
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

                    result[id] = StatusFrom(seenAgo, activeAgo);
                }
                return result;
            }
            catch (Exception ex) { if (IsSchemaMissing(ex)) _presenceColumnsOk = false; return null; }
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

        // ── Статус собеседника в шапке чата (в сети / бездействует N / был в сети N) ──
        private Label _lblChatPresence;

        /// <summary>Чей статус сейчас написан в шапке. Нужен, чтобы при
        /// переключении чата не оставить на экране подпись от прошлого
        /// собеседника.</summary>
        private int _presenceLabelPeer = -1;

        private void EnsureChatPresenceLabel()
        {
            if (_lblChatPresence != null) return;
            _lblChatPresence = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 9f),
                ForeColor = PresenceOffline,
                BackColor = pnlChatHeader.BackColor,   // сплошной фон шапки — без артефактов прозрачности
                Visible = false
            };
            pnlChatHeader.Controls.Add(_lblChatPresence);
            _lblChatPresence.BringToFront();
        }

        private static string HumanDur(int s)
        {
            if (s < 60) return "меньше минуты";
            int m = s / 60; if (m < 60) return $"{m} мин";
            int h = m / 60; if (h < 24) return $"{h} ч";
            int d = h / 24; return $"{d} дн";
        }

        private static string HumanAgo(int s)
        {
            if (s < 60) return "только что";
            int m = s / 60; if (m < 60) return $"{m} мин назад";
            int h = m / 60; if (h < 24) return $"{h} ч назад";
            int d = h / 24; return $"{d} дн назад";
        }

        private (string text, Color color)? ReadPeerPresenceText(int uid)
        {
            if (!_presenceColumnsOk || uid <= 0) return null;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS seen_ago, " +
                    "TIMESTAMPDIFF(SECOND, last_active, NOW()) AS active_ago FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                int seenAgo = r["seen_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["seen_ago"]);
                int activeAgo = r["active_ago"] == DBNull.Value ? int.MaxValue : Convert.ToInt32(r["active_ago"]);
                return PresenceText(StatusFrom(seenAgo, activeAgo), seenAgo, activeAgo);
            }
            catch (Exception ex) { if (IsSchemaMissing(ex)) _presenceColumnsOk = false; return null; }
        }

        /// <summary>Подпись и цвет для шапки чата по готовому статусу.</summary>
        private static (string text, Color color) PresenceText(int status, int seenAgo, int activeAgo)
        {
            if (status == 0) return ($"был(а) в сети {HumanAgo(seenAgo)}", PresenceOffline);
            if (status == 1) return ($"● бездействует {HumanDur(activeAgo)}", PresenceIdle);
            return ("● в сети", PresenceOnline);
        }

        /// <summary>Позиционирует ярлык статуса сразу за текстом заголовка чата.</summary>
        private void PositionChatPresence()
        {
            if (_lblChatPresence == null) return;
            Size sz = TextRenderer.MeasureText(lblChatTitle.Text, lblChatTitle.Font);
            int x = 16 + sz.Width + 12;                 // заголовок начинается с x=16
            int maxX = pnlChatHeader.Width - 300;       // не залезаем на кнопки справа
            if (maxX > 16 && x > maxX) x = maxX;
            _lblChatPresence.Location = new Point(x, 18);
        }

        /// <summary>Обновляет статус собеседника в шапке (для ЛС). Для групп/пустого
        /// выбора прячет. Читает БД в фоне, применяет на UI. Вызывается из OpenChat и
        /// по таймеру присутствия.</summary>
        private void UpdateChatHeaderPresence()
        {
            EnsureChatPresenceLabel();
            // Пока открыта строка поиска, статус не показываем: он делит место в шапке
            // с полем поиска, и в оконном режиме они накладывались.
            if (_searchRowOpen)
            {
                if (_lblChatPresence != null) _lblChatPresence.Visible = false;
                return;
            }
            int peer = _currentChatPartnerId;
            if (peer <= 0 || !_presenceColumnsOk)
            {
                if (_lblChatPresence != null) _lblChatPresence.Visible = false;
                _presenceLabelPeer = -1;
                return;
            }

            // Сменили собеседника — старую подпись убираем сразу, не дожидаясь
            // ответа базы: показывать «в сети» от предыдущего человека хуже,
            // чем не показывать ничего.
            if (_presenceLabelPeer != peer)
            {
                _lblChatPresence.Visible = false;
                _presenceLabelPeer = peer;
            }

            _ = Task.Run(() =>
            {
                var info = ReadPeerPresenceText(peer);
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (_currentChatPartnerId != peer) return;   // чат уже переключили
                        // Пока собеседник печатает, подпись занята надписью
                        // «печатает…» — не затираем её своим «в сети». Опрос идёт
                        // раз в несколько секунд и иначе гасил бы её на полпути.
                        if (TypingActive) return;
                        // Запрос не удался (оборвалась связь, не успел) — ОСТАВЛЯЕМ
                        // то, что написано. Раньше подпись при этом пряталась, и
                        // одного неудачного запроса хватало, чтобы статус исчез до
                        // следующей удачной сверки. Со стороны это выглядело как
                        // «статус пропал».
                        if (info == null) return;
                        _lblChatPresence.Text = info.Value.text;
                        _lblChatPresence.ForeColor = info.Value.color;
                        PositionChatPresence();
                        _lblChatPresence.Visible = true;
                        _lblChatPresence.BringToFront();
                    }));
                }
                catch { }
            });
        }

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

                // Участников берём только ЖИВЫХ.
                //
                // Строку в call_participants удаляет штатный выход из звонка, а
                // статус сессии закрывает уход последнего участника. Если
                // приложение закрыли крестиком, оно упало или пропала сеть —
                // не происходит ни того, ни другого, и запись остаётся в базе
                // навсегда. Отсюда и плашка «звонок идёт» без всякого звонка.
                //
                // Живость определяем по тому же last_seen, по которому
                // считается «в сети»: в звонке приложение работает и метку
                // обновляет, так что мёртвая запись отсеется за минуту.
                var names = new List<string>();
                using (var pc = new MySqlCommand(
                    "SELECT TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login FROM call_participants cp " +
                    "JOIN users u ON u.id = cp.user_id WHERE cp.call_id=@cid AND cp.left_at IS NULL " +
                    "AND u.last_seen IS NOT NULL AND TIMESTAMPDIFF(SECOND, u.last_seen, NOW()) <= 60 " +
                    "ORDER BY cp.joined_at ASC", conn))
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

                if (names.Count == 0)
                {
                    // Никого живого — звонка нет. Прибираем за собой, иначе
                    // плашка висела бы у всех, кто откроет эту переписку.
                    CloseDeadCall(conn, sid);
                    return;
                }

                callId = sid;
                text = $"📞 Звонок идёт ({names.Count}): {string.Join(", ", names)} — нажмите, чтобы присоединиться";
            }
            catch { text = null; callId = -1; }
        }

        /// <summary>
        /// Закрывает сессию, из которой все давно ушли, не сказав об этом базе.
        /// Тот же самый набор действий, что делает штатный выход последнего
        /// участника, — просто выполненный за него.
        /// </summary>
        private static void CloseDeadCall(MySqlConnection conn, int callId)
        {
            try
            {
                using (var d = new MySqlCommand(
                    "DELETE FROM call_participants WHERE call_id=@cid", conn))
                {
                    d.Parameters.AddWithValue("@cid", callId);
                    d.ExecuteNonQuery();
                }
                using var u = new MySqlCommand(
                    "UPDATE call_sessions SET status='ended', ended_at=NOW() " +
                    "WHERE id=@cid AND status IN ('ringing','active')", conn);
                u.Parameters.AddWithValue("@cid", callId);
                u.ExecuteNonQuery();
            }
            catch { }
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
