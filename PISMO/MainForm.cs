using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using NAudio.Wave;

namespace PISMO
{
    public partial class MainForm : Form
    {
        // ── Постраничная загрузка переписки (слабое железо / длинные чаты) ──
        // На открытии грузим ПОСЛЕДНИЕ MsgPageSize сообщений; при прокрутке вверх
        // догружаем ещё страницу, сохраняя позицию (не скидывая вниз).
        private const int MsgPageSize = 40;
        private int _dmLimit = MsgPageSize;         // сколько последних сообщений грузим сейчас (ЛС/группа)
        private bool _dmHasMore;                     // есть ли более старые сообщения
        private bool _dmLoadingOlder;                // идёт догрузка вверх
        private int _dmRestoreFromBottom = -1;       // держим позицию: расстояние от низа
        // Сколько сообщений было долистано в чате — чтобы при переоткрытии показать
        // столько же (ключ: "d{partnerId}" для ЛС, "g{groupId}" для группы).
        private Button _btnScrollDown;               // плавающая кнопка «вниз к новым»

        // ── Состояние ──────────────────────────────────────────────────────
        private int _currentChatPartnerId = -1;
        private string _currentChatPartnerName = "";
        private byte[] _pendingImageBytes = null;
        private PendingAttachment _pendingAttach = null;

        // true, когда открыт встроенный вид сервера (ЛС-панель скрыта). Тогда
        // входящие ЛС не помечаем прочитанными и уведомляем обо всех — чат не виден.
        private bool OnServerView => _railSelectedServerId > 0;

        // Групповые чаты
        private int _currentGroupId = -1;
        private string _currentGroupName = "";
        private readonly List<Panel> _groupPanels = new();

        private int _lastGroupMsgCount = 0;

        // Список карточек в сайдбаре (для обновления бейджей без перезагрузки)
        private readonly List<Panel> _userPanels = new();

        // Кнопка GIF-поиска в строке ввода (создаётся в коде, не в Designer)
        private Button btnGif;

        // Окно серверов (как в Discord)
        private ServersForm _serversForm;

        // Polling: обнаружение новых сообщений
        private System.Windows.Forms.Timer _pollTimer;
        private int _wsIdleTicks;   // тики опроса при живом WS (разряжаем до ~9с)
        private int _lastMsgCount = 0;
        private bool _pollBusy = false;
        private int _lastOpenSig = -1;   // число сообщений открытого чата на прошлом опросе (детект новых)
        private readonly Dictionary<int, int> _prevUnread = new();
        private Label _friendsBadge;      // красный бейдж на кнопке «Друзья»
        private int _prevFriendReq = -1;  // прошлое число входящих заявок (-1 = ещё не знаем)

        // Кеш метаданных переписки в памяти: при повторном открытии чата сообщения
        // показываются мгновенно из кеша, а свежие подгружаются в фоне (не вешая UI).
        private readonly Dictionary<int, DataTable> _msgMetaCache = new();   // partnerId -> meta
        private readonly Dictionary<int, (bool iBlocked, bool theyBlocked)> _blockCache = new();
        private readonly Dictionary<int, DataTable> _groupMetaCache = new(); // groupId -> meta
        // Что сейчас отрисовано в панели: чтобы не перерисовывать чат заново, если
        // данные не изменились (открытие рисует из кэша, потом из БД — второй раз
        // полное пересоздание пузырей лишнее и тормозит).
        private string _renderedChatKey;
        private string _renderedChatSig;
        private HashSet<int> _pinnedInView;   // id закреплённых сообщений текущего чата (2.0)
        private Dictionary<int, List<ReactionsRepository.Reaction>> _reactionsInView;  // реакции всех видимых сообщений (2.0)

        /// <summary>Сбросить кеш отрисовки, чтобы следующий Load перерисовал чат
        /// (нужно, когда изменились реакции/закрепления — данные сообщений те же).</summary>
        private void ForceMessageRerender() { _renderedChatKey = null; _renderedChatSig = null; }

        // created_at хранится во времени СЕРВЕРА БД (CURRENT_TIMESTAMP). Показываем во
        // времени ЗРИТЕЛЯ: сдвигаем на разницу поясов (смещение зрителя минус смещение
        // сервера от UTC). Смещение сервера узнаём один раз и кэшируем.
        private static int? _serverUtcOffsetSec;
        internal static DateTime ToViewerLocal(DateTime dbServerTime)
        {
            try
            {
                if (_serverUtcOffsetSec == null)
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySql.Data.MySqlClient.MySqlCommand(
                        "SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), NOW())", conn);
                    _serverUtcOffsetSec = Convert.ToInt32(cmd.ExecuteScalar());
                }
                int viewerOffsetSec = (int)DateTimeOffset.Now.Offset.TotalSeconds;
                return dbServerTime.AddSeconds(viewerOffsetSec - _serverUtcOffsetSec.Value);
            }
            catch { return dbServerTime; }
        }

        /// <summary>Дешёвая «подпись» переписки (без создания контролов) для сравнения.</summary>
        internal static string SigOf(DataTable dt)
        {
            if (dt == null) return "0";
            bool hasEdit = dt.Columns.Contains("edited_at");
            bool hasDel = dt.Columns.Contains("is_deleted");
            bool hasText = dt.Columns.Contains("text");
            bool hasRead = dt.Columns.Contains("is_read");
            var sb = new System.Text.StringBuilder();
            sb.Append(dt.Rows.Count);
            foreach (DataRow r in dt.Rows)
            {
                sb.Append('|').Append(r["id"]);
                if (hasEdit && r["edited_at"] != DBNull.Value) sb.Append('e').Append(r["edited_at"]);
                if (hasDel && r["is_deleted"] != DBNull.Value && Convert.ToBoolean(r["is_deleted"])) sb.Append('d');
                if (hasRead && r["is_read"] != DBNull.Value && Convert.ToBoolean(r["is_read"])) sb.Append('r');
                if (hasText && r["text"] != DBNull.Value) sb.Append('t').Append(r["text"].ToString().Length);
            }
            return sb.ToString();
        }

        // Голосовые сообщения
        private WaveInEvent _waveIn;
        private MemoryStream _audioStream;
        private WaveFileWriter _waveWriter;
        private WaveOutEvent _waveOut;

        // ════════════════════════════════════════════════════════════════
        //  ИНИЦИАЛИЗАЦИЯ
        // ════════════════════════════════════════════════════════════════
        public MainForm()
        {
            // TURN больше не используется — звонки идут через LiveKit (SFU).
            // (Раньше здесь запускался генератор TURN-кредов, всплывавший окном.)
            DeviceSettings.Load();

            // Применяем выбор видеокарты для кодирования (auto/high/integrated) к
            // приложению и WebView2. В фоне (ищет msedgewebview2.exe по диску).
            // Вступит в силу к первому звонку.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { GpuPreference.Apply(DeviceSettings.GpuEncodePref); } catch { }
            });

            InitializeComponent();

            // Докинг в WinForms идёт в ОБРАТНОМ z-order: контрол сзади докается первым и
            // забирает всю клиентскую область, контрол с индексом 0 — последним и получает
            // остаток. Поэтому Fill-панель сообщений должна быть ВПЕРЕДИ, иначе она
            // занимает всю высоту вместе с шапкой и её полоса прокрутки уходит ПОД
            // верхнюю панель («крышебойный»).
            try { pnlMessages.BringToFront(); } catch { }

            // Плавность отрисовки всего окна: буферизуем все панели/контролы (без
            // WS_EX_COMPOSITED — он давал мерцание). Новые контролы буферизуются
            // автоматически через хук ControlAdded.
            try { ChatScroll.EnableDoubleBufferDeep(this); } catch { }

            // Список контактов подключаем ЗДЕСЬ, а не в LoadConversations: в админском
            // режиме («Все пользователи») список строит LoadAllUsersForAdmin, который
            // Attach не вызывал — поэтому там не было плавной прокрутки. Attach
            // идемпотентен, повторные вызовы отсекаются.
            try { ChatScroll.Attach(pnlUserList); ChatScroll.KillHorizontal(pnlSidebar); } catch { }

            MediaCache.Init();
            EnableFileDrop(pnlMessages);   // перетаскивание файлов из проводника → прикрепить
            EnableFileDrop(txtMessage);
            ConnectionGuard.Init(this);   // окно «нет связи с БД» + авто-переподключение
            SetupPolling();
            BuildSidebarSearch();
            BuildVoiceDock();           // «Голосовая связь подключена» над профилем (как в Discord)
            BuildServerRail();          // левый рейл «Личные сообщения + серверы» (как в Discord)
            BuildPinsButton();          // 📌 кнопка «Закреплённые» в шапке чата (2.0)
            BuildTypingIndicator();     // «печатает…» (2.0)
            BuildMessageSearch();       // 🔍 поиск по открытому чату (2.0)
            BuildBackgroundStyling();   // мягкий градиент-подложка списка/чата (2.1.7)
            BuildReadAllButton();       // ✓✓ «прочитать все ЛС» в шапке сайдбара
            this.Load += MainForm_Load;
        }

        /// <summary>Кнопка «✓✓» в шапке списка чатов: помечает ВСЕ входящие ЛС
        /// прочитанными без захода в чаты — бейджи и уведомления гаснут.</summary>
        private void BuildReadAllButton()
        {
            try
            {
                var btn = new Button
                {
                    Text = "✓✓",
                    Dock = DockStyle.Right,
                    Width = 36,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(185, 187, 190),
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                btn.FlatAppearance.BorderSize = 0;
                new ToolTip().SetToolTip(btn, "Прочитать все личные сообщения");
                btn.Click += (s, e) =>
                {
                    int me = UserSession.EffectiveId;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            using var cmd = new MySqlCommand(
                                "UPDATE messages SET is_read=1 WHERE receiver_id=@me AND is_read=0", conn);
                            cmd.Parameters.AddWithValue("@me", me);
                            cmd.ExecuteNonQuery();
                        }
                        catch { }
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                LoadConversations();      // бейджи непрочитанных гаснут
                                PollTick(null, null);
                            }));
                        }
                        catch { }
                    });
                };
                pnlSidebarHeader.Controls.Add(btn);
                btn.BringToFront();
            }
            catch { }
        }

        // ── Мягкий вертикальный градиент-подложка (2.1.7, как в Discord) ──
        // Тонкий «перелив» фона ТОЛЬКО в области сообщений — там пузыри
        // непрозрачные. В список ЛС градиент НЕ ставим: карточки/лейблы там
        // прозрачные, и кастомный Paint + двойная буферизация давали «призраки»
        // текста (наложение). Сайдбар остаётся плоским (тема-независимым).
        private void BuildBackgroundStyling()
        {
            try
            {
                // Ровная подложка чата (как в Discord). Градиент на прокручиваемой
                // панели давал горизонтальные полосы при скролле — убран.
                EnableDoubleBuffer(pnlMessages);
                pnlMessages.BackColor = Theme.Map(Color.FromArgb(54, 57, 63));
            }
            catch { }
        }

        private static void EnableDoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(c, true);
            }
            catch { }
        }

        private static Color ShiftColor(Color c, int d) => Color.FromArgb(
            Math.Max(0, Math.Min(255, c.R + d)),
            Math.Max(0, Math.Min(255, c.G + d)),
            Math.Max(0, Math.Min(255, c.B + d)));

        private void ApplyGradientBackdrop(Control panel, Color darkBase)
        {
            if (panel == null) return;
            EnableDoubleBuffer(panel);
            try { panel.BackColor = Theme.Map(darkBase); } catch { }
            panel.Paint += (s, e) =>
            {
                try
                {
                    var rect = panel.ClientRectangle;
                    if (rect.Width <= 1 || rect.Height <= 1) return;
                    e.Graphics.ResetTransform();   // фикс-подложка, не зависит от прокрутки
                    var b = Theme.Map(darkBase);
                    Color top = ShiftColor(b, +9), bottom = ShiftColor(b, -11);
                    using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new Rectangle(0, 0, rect.Width, Math.Max(2, rect.Height)),
                        top, bottom, System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                    e.Graphics.FillRectangle(br, 0, 0, rect.Width, rect.Height);
                }
                catch { }
            };
            try { panel.Invalidate(); } catch { }
        }

        private Button _btnMsgSearch;
        private Button _btnMsgCalendar;      // 📅 переход к дате
        private bool _searchRowOpen;         // открыта ли строка поиска (прячем статус собеседника)

        /// <summary>Подгоняет ширину поля поиска под ширину шапки: в оконном режиме
        /// фиксированный отступ от правого края наезжал на имя собеседника и его статус.</summary>
        private void LayoutSearchRow()
        {
            if (_msgSearch == null || pnlChatHeader == null) return;
            try
            {
                int w = pnlChatHeader.ClientSize.Width;
                const int titleMin = 150;              // место под имя собеседника
                const int y = 12;                      // общая линия для всей строки
                int step = SearchBarUi.BtnW + SearchBarUi.Gap;   // единый шаг 30px

                // Раскладка справа налево: 🔍 ▼ ▲ 📅 [поле] [счётчик]
                // Счётчик вынесен ВЛЕВО от поля: между кнопками ему не хватало ширины,
                // и число обрезалось («11/» вместо «11/45»).
                _btnMsgSearch.Location     = new Point(w - 128, y);
                _btnMsgSearchNext.Location = new Point(w - 128 - step, y);
                _btnMsgSearchPrev.Location = new Point(w - 128 - step * 2, y);
                _btnMsgCalendar.Location   = new Point(w - 128 - step * 3, y);

                int boxRight = _btnMsgCalendar.Left - 4;
                int countW = _msgSearchCount.Width;
                int boxLeft = Math.Max(titleMin + countW + 6, boxRight - 240);
                int boxW = Math.Max(70, boxRight - boxLeft);
                _msgSearch.Bounds = new Rectangle(boxLeft, y, boxW, SearchBarUi.BoxH);
                _msgSearchCount.Location = new Point(boxLeft - countW - 6, y + 2);
            }
            catch { }
        }
        private TextBox _msgSearch;
        private Label _msgSearchCount;

        /// <summary>Поиск по открытому чату: кнопка 🔍 в шапке разворачивает поле;
        /// по вводу подсвечивает совпадения; стрелки ▲/▼ (и Enter/Shift+Enter)
        /// ходят по найденному; клик по счётчику — выпадающий список совпадений
        /// (полный текст сообщения + дата отправки).</summary>
        private void BuildMessageSearch()
        {
            try
            {
                _btnMsgSearch = SearchBarUi.Make(SearchBarUi.Icon.Magnifier);
                _btnMsgSearch.Location = new Point(pnlChatHeader.Width - 128, 12);
                _btnMsgSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                new ToolTip().SetToolTip(_btnMsgSearch, "Поиск по чату");

                Button MkNav(SearchBarUi.Icon icon, int right, string tip)
                {
                    var b = SearchBarUi.Make(icon);
                    b.Location = new Point(pnlChatHeader.Width - right, 12);
                    b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                    b.Visible = false;
                    new ToolTip().SetToolTip(b, tip);
                    return b;
                }
                _btnMsgSearchNext = MkNav(SearchBarUi.Icon.Down, 152, "Следующее совпадение (Enter)");
                _btnMsgSearchPrev = MkNav(SearchBarUi.Icon.Up, 176, "Предыдущее совпадение (Shift+Enter)");
                // 📅 — переход к первому сообщению за выбранную дату.
                _btnMsgCalendar = MkNav(SearchBarUi.Icon.Calendar, 240, "Перейти к дате");
                _btnMsgCalendar.Click += (s2, e2) =>
                    DatePickerPopup.Show(_btnMsgCalendar, DateTime.Today, JumpToDate);
                _btnMsgSearchPrev.Click += (s, e) => GoToSearchMatch(-1);
                _btnMsgSearchNext.Click += (s, e) => GoToSearchMatch(+1);

                _msgSearch = new TextBox
                {
                    Visible = false, BorderStyle = BorderStyle.FixedSingle,
                    BackColor = Color.FromArgb(30, 31, 34), ForeColor = Color.White,
                    Font = new Font("Segoe UI", 10f), PlaceholderText = "Поиск в переписке…",
                    // Ширина 190: поле занимает Width-490..Width-300, дальше 📅, затем счётчик.
                    Size = new Size(140, 22), Location = new Point(pnlChatHeader.Width - 386, 13),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                _msgSearchCount = new Label
                {
                    Visible = false, AutoSize = false, Size = new Size(40, 20),
                    Location = new Point(pnlChatHeader.Width - 216, 14), Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    ForeColor = Color.FromArgb(150, 152, 158), Font = new Font("Segoe UI", 8f),
                    TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent,
                    Cursor = Cursors.Hand
                };
                SearchBarUi.StyleBox(_msgSearch, "Поиск в переписке…");
                SearchBarUi.StyleCount(_msgSearchCount);
                new ToolTip().SetToolTip(_msgSearchCount, "Список найденных сообщений");
                _msgSearchCount.Click += (s, e) => ShowSearchResults();

                void SetSearchVisible(bool show)
                {
                    _searchRowOpen = show;
                    // Статус «был(а) в сети…» и поле поиска делят одно место в шапке:
                    // в оконном режиме они накладывались друг на друга.
                    try { if (_lblChatPresence != null) _lblChatPresence.Visible = !show; } catch { }
                    _msgSearch.Visible = show;
                    LayoutSearchRow();
                    _msgSearchCount.Visible = show;
                    _btnMsgSearchPrev.Visible = show;
                    _btnMsgSearchNext.Visible = show;
                    _btnMsgCalendar.Visible = show;
                    if (show) _msgSearch.Focus();
                    else
                    {
                        try { _searchResultsPopup?.Close(); } catch { }
                        _msgSearch.Clear();
                        HighlightSearch("");
                    }
                }

                _btnMsgSearch.Click += (s, e) => SetSearchVisible(!_msgSearch.Visible);
                _msgSearch.TextChanged += (s, e) => HighlightSearch(_msgSearch.Text);
                _msgSearch.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Escape) { SetSearchVisible(false); e.SuppressKeyPress = true; }
                    else if (e.KeyCode == Keys.Enter)
                    {
                        GoToSearchMatch(e.Shift ? -1 : +1);
                        e.SuppressKeyPress = true;   // без «динь» системного бипа
                    }
                };

                pnlChatHeader.Controls.Add(_msgSearch);
                pnlChatHeader.Controls.Add(_msgSearchCount);
                pnlChatHeader.Controls.Add(_btnMsgSearchPrev);
                pnlChatHeader.Controls.Add(_btnMsgSearchNext);
                pnlChatHeader.Controls.Add(_btnMsgCalendar);
                pnlChatHeader.Controls.Add(_btnMsgSearch);
                // lblChatTitle докнут на всю шапку и лежит ВПЕРЕДИ по z-order, поэтому
                // каждый элемент поиска обязан поднять себя сам — иначе он окажется за
                // подписью чата и будет невидим (так и вышло с 📅).
                _msgSearch.BringToFront(); _msgSearchCount.BringToFront();
                _btnMsgSearchPrev.BringToFront(); _btnMsgSearchNext.BringToFront();
                _btnMsgCalendar.BringToFront();
                _btnMsgSearch.BringToFront();
                pnlChatHeader.Resize += (s, e) => { if (_searchRowOpen) LayoutSearchRow(); };
            }
            catch { }
        }

        // ── Поиск по чату: список совпадений + навигация (2.1) ──
        private readonly List<Panel> _searchMatches = new();
        private int _searchIndex = -1;
        private Button _btnMsgSearchPrev, _btnMsgSearchNext;
        private Form _searchResultsPopup;

        /// <summary>Подсветка сообщений с текстом query в открытом чате.
        /// Строит список совпадений (сверху вниз) и встаёт на первое.</summary>
        private void HighlightSearch(string query)
        {
            try
            {
                query = (query ?? "").Trim();
                _searchMatches.Clear();
                _searchIndex = -1;
                foreach (Control c in pnlMessages.Controls)
                {
                    if (c is not Panel b || b.AccessibleDescription == null) continue;
                    bool isMine = b.Tag is bool mine && mine;
                    // Восстанавливаем исходный фон пузыря с учётом темы (в светлой
                    // теме нейтральный фон собеседника перекрашивается, иначе после
                    // поиска пузыри стали бы тёмными на светлом фоне).
                    Color orig = Theme.Map(isMine ? Color.FromArgb(88, 101, 242) : Color.FromArgb(48, 51, 58));
                    if (query.Length > 0 && b.AccessibleDescription.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        _searchMatches.Add(b);
                    else { b.BackColor = orig; b.Invalidate(); }
                }
                // Порядок — визуальный, сверху вниз (Controls может не совпадать).
                _searchMatches.Sort((a, b2) => a.Top.CompareTo(b2.Top));
                if (_searchMatches.Count > 0) _searchIndex = 0;
                RepaintSearchMatches();
                UpdateSearchNavUi(query.Length > 0);
                if (_searchIndex >= 0)
                    try { pnlMessages.ScrollControlIntoView(_searchMatches[_searchIndex]); } catch { }
            }
            catch { }
        }

        /// <summary>Текущее совпадение — ярче остальных.</summary>
        private void RepaintSearchMatches()
        {
            for (int i = 0; i < _searchMatches.Count; i++)
            {
                var b = _searchMatches[i];
                if (b == null || b.IsDisposed) continue;
                b.BackColor = i == _searchIndex
                    ? Color.FromArgb(124, 162, 80)    // текущее
                    : Color.FromArgb(83, 108, 60);    // остальные
                b.Invalidate();
            }
        }

        private void UpdateSearchNavUi(bool active)
        {
            if (_msgSearchCount != null)
                _msgSearchCount.Text = !active ? ""
                    : _searchMatches.Count == 0 ? "0 найд."
                    : $"{_searchIndex + 1}/{_searchMatches.Count} ▾";
            // Enabled НЕ используем: у отключённой кнопки Windows рисует текст
            // системным «серым», и на тёмной шапке стрелки становились почти чёрными.
            // Вместо этого держим кнопки включёнными и просто приглушаем цвет —
            // сам переход всё равно ничего не делает, когда совпадений нет.
            bool canNav = active && _searchMatches.Count > 0;
            var navColor = canNav ? SearchBarUi.Fg : SearchBarUi.FgDim;
            if (_btnMsgSearchPrev != null) { _btnMsgSearchPrev.Enabled = true; _btnMsgSearchPrev.ForeColor = navColor; }
            if (_btnMsgSearchNext != null) { _btnMsgSearchNext.Enabled = true; _btnMsgSearchNext.ForeColor = navColor; }
        }

        /// <summary>Перейти к следующему/предыдущему совпадению (с зацикливанием).</summary>
        private void GoToSearchMatch(int delta)
        {
            try
            {
                // Сообщение могли удалить/перерисовать — чистим мёртвые панели.
                _searchMatches.RemoveAll(p => p == null || p.IsDisposed);
                if (_searchMatches.Count == 0) { UpdateSearchNavUi(true); return; }
                _searchIndex = ((_searchIndex + delta) % _searchMatches.Count + _searchMatches.Count) % _searchMatches.Count;
                RepaintSearchMatches();
                UpdateSearchNavUi(true);
                pnlMessages.ScrollControlIntoView(_searchMatches[_searchIndex]);
            }
            catch { }
        }

        /// <summary>Выпадающий список найденных: полный текст сообщения + дата
        /// отправки; клик по строке — переход к сообщению в чате.</summary>
        private void ShowSearchResults()
        {
            try
            {
                if (_searchResultsPopup != null && !_searchResultsPopup.IsDisposed)
                { try { _searchResultsPopup.Close(); } catch { } _searchResultsPopup = null; return; }
                _searchMatches.RemoveAll(p => p == null || p.IsDisposed);
                if (_searchMatches.Count == 0) return;

                var pop = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    BackColor = Color.FromArgb(30, 31, 34),
                    Size = new Size(380, Math.Min(408, 14 + _searchMatches.Count * 66))
                };
                var list = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoScroll = true,
                    BackColor = Color.FromArgb(30, 31, 34),
                    Padding = new Padding(6)
                };
                for (int i = 0; i < _searchMatches.Count; i++)
                {
                    var b = _searchMatches[i];
                    string full = b.AccessibleDescription ?? "";
                    // Дата отправки кладётся пузырю при построении (см. загрузчики).
                    string when = string.IsNullOrEmpty(b.AccessibleDefaultActionDescription)
                        ? "" : b.AccessibleDefaultActionDescription;

                    var card = new Panel
                    {
                        Width = 348, Height = 60,
                        BackColor = i == _searchIndex ? Color.FromArgb(56, 66, 46) : Color.FromArgb(43, 45, 49),
                        Cursor = Cursors.Hand,
                        Margin = new Padding(0, 0, 0, 6)
                    };
                    card.Controls.Add(new Label
                    {
                        Text = when, ForeColor = Color.FromArgb(140, 142, 148),
                        Font = new Font("Segoe UI", 7.5f), AutoSize = false,
                        Size = new Size(330, 14), Location = new Point(8, 4)
                    });
                    card.Controls.Add(new Label
                    {
                        Text = full, ForeColor = Color.White,
                        Font = new Font("Segoe UI", 9f), AutoSize = false,
                        Size = new Size(332, 38), Location = new Point(8, 19), AutoEllipsis = true
                    });
                    int idx = i;
                    void Go(object s, EventArgs e)
                    {
                        _searchIndex = idx;
                        RepaintSearchMatches();
                        UpdateSearchNavUi(true);
                        try { pnlMessages.ScrollControlIntoView(_searchMatches[idx]); } catch { }
                        try { pop.Close(); } catch { }
                    }
                    card.Click += Go;
                    foreach (Control cc in card.Controls) { cc.Click += Go; cc.Cursor = Cursors.Hand; }
                    list.Controls.Add(card);
                }
                pop.Controls.Add(list);

                var anchor = (Control)(_msgSearch != null && _msgSearch.Visible ? _msgSearch : _msgSearchCount);
                pop.Location = anchor.PointToScreen(new Point(0, anchor.Height + 4));
                pop.Deactivate += (s, e) => { try { pop.Close(); } catch { } };
                pop.FormClosed += (s, e) => _searchResultsPopup = null;
                _searchResultsPopup = pop;
                pop.Show(this);
            }
            catch { }
        }

        private Label _lblTyping;
        private System.Windows.Forms.Timer _typingHideTimer;
        private DateTime _lastTypingSent = DateTime.MinValue;

        /// <summary>Индикатор «печатает…» в шапке чата + отправка своего статуса
        /// набора по WS (не чаще раза в 2 c).</summary>
        private void BuildTypingIndicator()
        {
            try
            {
                _lblTyping = new Label
                {
                    Text = "",
                    Font = new Font("Segoe UI Italic", 8.5f, FontStyle.Italic),
                    ForeColor = Color.FromArgb(150, 152, 158),
                    AutoSize = false,
                    Size = new Size(220, 20),
                    Location = new Point(pnlChatHeader.Width - 290, 14),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    TextAlign = ContentAlignment.MiddleRight,
                    BackColor = Color.Transparent,
                    Visible = false
                };
                pnlChatHeader.Controls.Add(_lblTyping);
                _lblTyping.BringToFront();

                _typingHideTimer = new System.Windows.Forms.Timer { Interval = 4000 };
                _typingHideTimer.Tick += (s, e) => { _typingHideTimer.Stop(); if (_lblTyping != null) _lblTyping.Visible = false; };

                txtMessage.TextChanged += (s, e) =>
                {
                    if (_editMsgId >= 0) return;                 // при редактировании не шлём
                    if (string.IsNullOrEmpty(txtMessage.Text)) return;
                    if ((DateTime.UtcNow - _lastTypingSent).TotalSeconds < 2) return;
                    _lastTypingSent = DateTime.UtcNow;
                    try
                    {
                        if (_currentGroupId > 0)
                            WebSocketSignalingClient.Instance.SendMessage("typing", 0, _currentGroupId, "group");
                        else if (_currentChatPartnerId > 0)
                            WebSocketSignalingClient.Instance.SendMessage("typing", _currentChatPartnerId, UserSession.EffectiveId, "direct");
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// <summary>Показать «X печатает…» на пару секунд (по WS-событию).</summary>
        private void ShowTyping(int fromUid, bool group, int groupOrPeer)
        {
            if (_lblTyping == null) return;
            bool relevant = group ? (_currentGroupId == groupOrPeer) : (_currentChatPartnerId == fromUid);
            if (!relevant || fromUid == UserSession.EffectiveId) return;
            string name = GetNameFromCards(fromUid);
            if (string.IsNullOrWhiteSpace(name)) name = "Собеседник";
            _lblTyping.Text = $"✍ {name} печатает…";
            _lblTyping.Visible = true;
            _typingHideTimer.Stop();
            _typingHideTimer.Start();
        }

        private Button _btnPins;

        /// <summary>Кнопка «📌 Закреплённые» в шапке чата — открывает список
        /// закреплённых сообщений текущего диалога/группы.</summary>
        private void BuildPinsButton()
        {
            try
            {
                _btnPins = new Button
                {
                    Text = "📌",
                    Font = new Font("Segoe UI Emoji", 11f),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.FromArgb(200, 202, 208),
                    BackColor = Color.FromArgb(47, 49, 54),
                    Size = new Size(40, 34),
                    // Правее (Width-38..Width) живёт докнутая кнопка звонка 📞 —
                    // не заезжаем на неё (раньше 📌 стояла на Width-52 и перекрывала).
                    Location = new Point(pnlChatHeader.Width - 96, 7),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                _btnPins.FlatAppearance.BorderSize = 0;
                new ToolTip().SetToolTip(_btnPins, "Закреплённые сообщения");
                _btnPins.Click += (s, e) => ShowPinnedList();
                pnlChatHeader.Controls.Add(_btnPins);
                _btnPins.BringToFront();
            }
            catch { }
        }

        /// <summary>Всплывающий список закреплённых сообщений текущего чата.</summary>
        private void ShowPinnedList()
        {
            List<PinsRepository.PinnedItem> items;
            if (_currentGroupId > 0) items = PinsRepository.ForGroup(_currentGroupId);
            else if (_currentChatPartnerId > 0) items = PinsRepository.ForDirect(UserSession.EffectiveId, _currentChatPartnerId);
            else return;

            var pop = new Form
            {
                Text = "📌 Закреплённые сообщения",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 440),
                BackColor = Color.FromArgb(49, 51, 56)
            };
            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Color.FromArgb(49, 51, 56),
                Padding = new Padding(10)
            };
            if (items.Count == 0)
            {
                list.Controls.Add(new Label
                {
                    Text = "В этом чате нет закреплённых сообщений.\nПКМ по сообщению → 📌 Закрепить.",
                    ForeColor = Color.FromArgb(150, 152, 158),
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9.5f)
                });
            }
            foreach (var it in items)
            {
                string body;
                try { body = Crypto.Dec(it.TextCipher ?? ""); } catch { body = ""; }
                if (string.IsNullOrWhiteSpace(body)) body = "[вложение]";
                var card = new Panel { Width = 388, Height = 60, BackColor = Color.FromArgb(43, 45, 49), Margin = new Padding(0, 0, 0, 6) };
                card.Controls.Add(new Label
                {
                    Text = it.Sender, ForeColor = Color.FromArgb(120, 140, 255),
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    AutoSize = false, Size = new Size(360, 16), Location = new Point(10, 6), AutoEllipsis = true
                });
                card.Controls.Add(new Label
                {
                    Text = body.Length > 120 ? body[..120] + "…" : body,
                    ForeColor = Color.White, AutoSize = false, Size = new Size(368, 32),
                    Location = new Point(10, 24), Font = new Font("Segoe UI", 9f), AutoEllipsis = true
                });
                list.Controls.Add(card);
            }
            pop.Controls.Add(list);
            pop.ShowDialog(this);
        }

        private TextBox _convSearch;
        private Panel _convSearchHost;   // контейнер поиска (нужен для z-порядка при вставке дока)

        /// <summary>Поле поиска чатов над списком диалогов в боковой панели.</summary>
        private void BuildSidebarSearch()
        {
            var host = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(32, 34, 37), Padding = new Padding(8, 5, 8, 5) };
            _convSearchHost = host;
            _convSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                PlaceholderText = "Поиск чатов…"
            };
            _convSearch.TextChanged += (s, e) => FilterConversations(_convSearch.Text);
            host.Controls.Add(_convSearch);
            pnlSidebar.Controls.Add(host);

            // Док обрабатывается от ВЫСШЕГО индекса к низшему. Чтобы список (Fill) занял
            // остаток (а не всю высоту с наложением шапки/поиска), задаём порядок явно:
            //   шапка (3, к верхнему краю) -> поиск (2, под шапкой) -> футер (1, низ) -> список (0, Fill последним).
            pnlSidebar.Controls.SetChildIndex(pnlUserList, 0);
            try { pnlSidebar.Controls.SetChildIndex(pnlSidebarFooter, 1); } catch { }
            pnlSidebar.Controls.SetChildIndex(host, 2);
            pnlSidebar.Controls.SetChildIndex(pnlSidebarHeader, 3);
        }

        /// <summary>Фильтрует список диалогов/групп по подстроке (имя — в AccessibleName карточки).</summary>
        private void FilterConversations(string q)
        {
            q = (q ?? "").Trim().ToLowerInvariant();
            pnlUserList.SuspendLayout();
            foreach (var p in _userPanels)
                p.Visible = q.Length == 0 || (p.AccessibleName ?? "").ToLowerInvariant().Contains(q);
            foreach (var p in _groupPanels)
                p.Visible = q.Length == 0 || (p.AccessibleName ?? "").ToLowerInvariant().Contains(q);
            pnlUserList.ResumeLayout();
        }

        private void TrayMenuOpen_Click(object sender, EventArgs e)
        {
            // Показать окно и восстановить из трея. НЕ трогаем ShowInTaskbar
            // (его присвоение пересоздаёт хендл окна и ломало последующий крестик).
            // Порядок: сперва нормальное состояние, затем показ и активация.
            if (this.WindowState == FormWindowState.Minimized)
                this.WindowState = FormWindowState.Normal;
            if (!this.Visible) this.Show();
            try { this.Activate(); this.BringToFront(); } catch { }
        }

        private void TrayMenuExit_Click(object sender, EventArgs e)
        {
            // Полный выход из трея.
            _reallyExit = true;
            try { _pollTimer?.Stop(); } catch { }
            try { _trayIcon.Visible = false; } catch { }
            this.Close();   // OnFormClosing пропустит (см. _reallyExit) → OnFormClosed → Environment.Exit
        }

        private void pnlUserList_Resize(object sender, EventArgs e)
        {
            // Подгоняем ширину карточек ПОД клиентскую область панели (уже без
            // вертикальной полосы) — тогда горизонтального скролла не возникает.
            try
            {
                // Считаем от СТАБИЛЬНОЙ Width (не от ClientSize!) и ВСЕГДА резервируем
                // ширину вертикальной полосы. ClientSize «схлопывается» в момент
                // появления вертикального скролла ПОСРЕДИ загрузки БЕЗ события Resize —
                // из-за этого карточки, посчитанные по прежней (широкой) ClientSize,
                // потом вылезали и давал о себе знать горизонтальный скролл.
                int avail = Math.Max(80, pnlUserList.Width
                                          - pnlUserList.Padding.Horizontal
                                          - SystemInformation.VerticalScrollBarWidth);
                foreach (Control ctrl in pnlUserList.Controls)
                {
                    // Заголовки/подсказки («ГРУППЫ», hint админа) — прямые Label в списке.
                    // Их тоже подгоняем, иначе длинный текст даёт горизонтальный скролл.
                    if (ctrl is Label direct)
                        direct.Width = avail - direct.Margin.Horizontal;

                    if (ctrl is Button btn)   // кнопка «Друзья» (+ бейдж заявок на ней)
                    {
                        btn.Width = avail - btn.Margin.Horizontal;
                        foreach (Control c in btn.Controls)
                            if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                                lb.Location = new Point(Math.Max(0, btn.Width - 32), 8);
                    }
                    if (ctrl is Panel pnl)
                    {
                        pnl.Width = avail - pnl.Margin.Horizontal;
                        foreach (Control c in pnl.Controls)
                        {
                            // бейджи с красным фоном — корректируем позицию
                            if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                                lb.Location = new Point(Math.Max(0, pnl.Width - 34), 22);

                            // подписи и превью — корректируем ширину
                            if (c is Label lbl && lbl.AutoEllipsis)
                                lbl.Size = new Size(Math.Max(40, pnl.Width - 90), lbl.Height);
                        }
                    }
                }
            }
            catch { }
        }

        private void pnlChatHeader_Paint(object sender, PaintEventArgs e)
        {
            // Рисуем тонкую разделительную линию внизу заголовка чата
            var p = sender as Panel ?? pnlChatHeader;
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLine(pen, 0, p.Height - 1, p.Width, p.Height - 1);
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            // КРИТИЧНО для пуш-уведомлений: NotifyIcon.ShowBalloonTip() молча
            // ничего не показывает, если у иконки не задан Icon. Раньше _trayIcon
            // создавался без иконки — отсюда «уведомления не приходят». Назначаем
            // иконку приложения и трею, и самой форме.
            try
            {
                var icoPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pismo.ico");
                System.Drawing.Icon appIcon = System.IO.File.Exists(icoPath)
                    ? new System.Drawing.Icon(icoPath)
                    : System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null)
                {
                    this.Icon = appIcon;
                    if (_trayIcon != null) { _trayIcon.Icon = appIcon; _trayIcon.Visible = true; }
                }
            }
            catch { /* иконка не критична для работы */ }

            lblCurrentUser.Text = UserSession.UserName;

            // Аватарки: перерисовываем карточки, когда аватар загрузился в фоне;
            // двойной клик по своему имени — сменить аватар.
            AvatarStore.AvatarLoaded += uid =>
            {
                try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => InvalidateAvatarFor(uid))); }
                catch { }
            };
            try
            {
                lblCurrentUser.Cursor = Cursors.Hand;
                lblCurrentUser.DoubleClick -= LblCurrentUser_DoubleClick;
                lblCurrentUser.DoubleClick += LblCurrentUser_DoubleClick;
            }
            catch { }

            // Кружок-аватар возле имени аккаунта (видно сразу где менять аватар).
            try
            {
                pnlMyAvatar.Paint += PnlMyAvatar_Paint;
                pnlMyAvatar.Click += (s, e) => OpenProfile();
                var ttAv = new ToolTip();
                ttAv.SetToolTip(pnlMyAvatar, "Нажмите, чтобы открыть профиль");
                AvatarStore.EnsureLoaded(UserSession.EffectiveId);
            }
            catch { }

            // Кнопка GIF в строке ввода (слева от «Отправить»).
            try
            {
                btnGif = new Button
                {
                    Text = "GIF",
                    Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                    Size = new Size(44, 40),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(64, 68, 75),
                    ForeColor = Color.FromArgb(220, 221, 222),
                    Cursor = Cursors.Hand
                };
                btnGif.FlatAppearance.BorderSize = 0;
                btnGif.Click += (s, e) => OpenGifPicker();
                pnlInputBar.Controls.Add(btnGif);
                btnGif.BringToFront();
                pnlInputBar_Resize(this, EventArgs.Empty);
            }
            catch { }

            // Доступ к серверам теперь через левый рейл (BuildServerRail) — как в Discord,
            // поэтому отдельная кнопка «Серверы» в шапке сайдбара больше не нужна.

            InitMessageActions();
            StartPresence();

            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();
            pnlInputBar_Resize(this, EventArgs.Empty);

            _ = WebSocketSignalingClient.Instance.ConnectAsync(UserSession.EffectiveId);
            WebSocketSignalingClient.Instance.OnMessageReceived += (type, senderId, sessionId, payload) =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (type == "new_message")
                        {
                            // Пришло событие — открытый чат перезагружаем напрямую
                            // (без сверки COUNT: при медленной удалённой БД она
                            // промахивалась и сообщение появлялось лишь при переоткрытии).
                            // НО не трогаем ЛС-чат, если сейчас открыт встроенный вид
                            // сервера: иначе скрытый чат перезагружался и помечал
                            // входящие прочитанными → пуш в трей по ЛС не приходил.
                            if (!OnServerView)
                            {
                                if (_currentGroupId >= 0) LoadGroupMessages();
                                else if (_currentChatPartnerId >= 0) LoadMessages();
                            }
                            PollTick(null, null); // непрочитанные/бейджи
                        }
                        else if (type == "auth_error")
                        {
                            System.Diagnostics.Debug.WriteLine("[WS] register отклонён сервером (auth_error)");
                        }
                        else if (type == "read")
                        {
                            // Собеседник (senderId) прочитал мои сообщения — обновим галочки.
                            if (senderId == _currentChatPartnerId) LoadMessages();
                        }
                        else if (type == "reaction")
                        {
                            // Кто-то поставил/снял реакцию — перерисуем открытый чат.
                            ForceMessageRerender();
                            if (_currentGroupId > 0) LoadGroupMessages();
                            else if (_currentChatPartnerId > 0) LoadMessages();
                        }
                        else if (type == "edit")
                        {
                            // Собеседник отредактировал/удалил сообщение — перегружаем
                            // открытый чат сразу (раньше правка была видна только после
                            // нового сообщения или переоткрытия чата).
                            ForceMessageRerender();
                            if (_currentGroupId > 0) LoadGroupMessages();
                            else if (_currentChatPartnerId > 0) LoadMessages();
                        }
                        else if (type == "typing")
                        {
                            // «печатает…»: для группы sessionId=groupId, для лички
                            // senderId=печатающий.
                            bool grp = payload == "group";
                            ShowTyping(senderId, grp, grp ? sessionId : senderId);
                        }
                        else if (type == "mention")
                        {
                            // payload: serverId|serverName|channelName
                            HandleMentionNotification(payload);
                        }
                        else if (type == "reply")
                        {
                            // Кто-то ответил на моё сообщение в канале сервера.
                            HandleMentionNotification(payload, isReply: true);
                        }
                        else if (type == "profile_updated")
                        {
                            // sessionId = uid пользователя, обновившего профиль.
                            int uid2 = sessionId;
                            if (uid2 > 0)
                            {
                                AvatarStore.Invalidate(uid2);
                                AvatarStore.EnsureLoaded(uid2);
                                InvalidateAvatarFor(uid2);
                                try { LoadConversations(); } catch { }
                                try { if (_activeCall != null && !_activeCall.IsDisposed) _activeCall.OnRemoteProfileUpdated(uid2); } catch { }
                            }
                        }
                    });
                }
                catch { }
            };

            // Светлая тема (2.0): перекрашиваем дерево контролов; для тёмной —
            // no-op. ControlAdded-хук внутри Apply покроет пузыри/карточки,
            // создаваемые позже.
            try { Theme.Apply(this); } catch { }
        }

        /// <summary>Трей-уведомление об упоминании на сервере (если сервер не
        /// заглушён). payload: "serverId|serverName|channelName".</summary>
        private void HandleMentionNotification(string payload, bool isReply = false)
        {
            try
            {
                var parts = (payload ?? "").Split('|');
                if (parts.Length < 3) return;
                if (!int.TryParse(parts[0], out int serverId)) return;

                // Проверка «заглушён ли сервер» — в фоне (не держим UI-поток на БД).
                int me = UserSession.EffectiveId;
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool muted = false;
                    try
                    {
                        using var conn = DBHelper.OpenConnection();
                        using var cmd = new MySqlCommand(
                            "SELECT muted_notifs FROM server_members WHERE server_id=@s AND user_id=@u", conn);
                        cmd.Parameters.AddWithValue("@s", serverId);
                        cmd.Parameters.AddWithValue("@u", me);
                        var o = cmd.ExecuteScalar();
                        muted = o != null && o != DBNull.Value && Convert.ToInt32(o) == 1;
                    }
                    catch { }
                    if (muted || IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try { Sounds.Message(); } catch { }
                            PushNotify(isReply ? "PISMO — ответ" : "PISMO — упоминание",
                                isReply ? $"Ответ на ваше сообщение: {parts[1]} · #{parts[2]}"
                                        : $"Вас упомянули: {parts[1]} · #{parts[2]}");
                            try { FlashWindow(this.Handle, true); } catch { }
                        }));
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>Перерисовать аватар в карточке пользователя uid (после
        /// фоновой загрузки аватара).</summary>
        private void InvalidateAvatarFor(int uid)
        {
            foreach (var p in _userPanels)
            {
                if (p.Tag is int id && id == uid)
                    foreach (Control c in p.Controls)
                        if (c is Panel av) { try { av.Invalidate(); } catch { } }
            }
            // Свой кружок-аватар в футере.
            if (uid == UserSession.EffectiveId)
                try { pnlMyAvatar?.Invalidate(); } catch { }
        }

        /// <summary>Рисует круглый аватар текущего пользователя в футере сайдбара.</summary>
        private void PnlMyAvatar_Paint(object sender, PaintEventArgs e)
        {
            int uid = UserSession.EffectiveId;
            int size = Math.Min(pnlMyAvatar.Width, pnlMyAvatar.Height) - 2;
            int x = (pnlMyAvatar.Width - size) / 2, y = (pnlMyAvatar.Height - size) / 2;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (!AvatarStore.DrawAvatar(e.Graphics, uid, x, y, size))
            {
                e.Graphics.FillEllipse(new SolidBrush(GetAvatarColor(uid)), x, y, size, size);
                string nm = UserSession.UserName ?? "";
                string letter = nm.Length > 0 ? nm[0].ToString().ToUpper() : "?";
                using var f = new Font("Segoe UI Black", 13f, FontStyle.Bold);
                var sz = e.Graphics.MeasureString(letter, f);
                e.Graphics.DrawString(letter, f, Brushes.White,
                    x + (size - sz.Width) / 2, y + (size - sz.Height) / 2);
            }
        }

        private void LblCurrentUser_DoubleClick(object sender, EventArgs e) => OpenProfile();

        /// <summary>Открывает окно редактирования профиля и обновляет шапку/аватар.</summary>
        private void OpenProfile()
        {
            using var pf = new ProfileForm(UserSession.EffectiveId);
            pf.ShowDialog(this);
            if (pf.Saved)
            {
                lblCurrentUser.Text = UserSession.UserName;
                AvatarStore.Invalidate(UserSession.EffectiveId);
                AvatarStore.EnsureLoaded(UserSession.EffectiveId);
                InvalidateAvatarFor(UserSession.EffectiveId);
                try { pnlMyAvatar?.Invalidate(); } catch { }
                try { LoadConversations(); } catch { }

                // Применяем изменения в активном звонке (имя+аватар) и сообщаем
                // другим клиентам, чтобы у них обновился аватар в чатах/звонке.
                try { if (_activeCall != null && !_activeCall.IsDisposed) _activeCall.ApplyMyProfileChanged(); } catch { }
                try { WebSocketSignalingClient.Instance.SendMessage("profile_updated", 0, UserSession.EffectiveId, ""); } catch { }
            }
        }

        /// <summary>Открывает окно поиска гифок (Giphy) и отправляет выбранную.</summary>
        private void OpenGifPicker()
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите чат или группу.", "PISMO");
                return;
            }
            var picker = new GifPickerForm();
            try
            {
                var loc = PointToScreen(new Point(pnlInputBar.Left, pnlInputBar.Top));
                picker.StartPosition = FormStartPosition.Manual;
                picker.Location = new Point(Math.Max(0, loc.X + 40), Math.Max(0, loc.Y - 540));
            }
            catch { }
            picker.GifSelected += url => _ = SendGifByUrlAsync(url);
            picker.Show(this);
        }

        private async System.Threading.Tasks.Task SendGifByUrlAsync(string url)
        {
            try
            {
                var bytes = await GiphyClient.DownloadAsync(url);
                if (bytes == null || bytes.Length == 0)
                {
                    MessageBox.Show("Не удалось загрузить гифку.", "PISMO");
                    return;
                }
                if (IsDisposed) return;
                // Отправляем как изображение (анимация определяется по содержимому GIF).
                if (_currentGroupId >= 0) SendGroupMessage("", bytes);
                else if (_currentChatPartnerId >= 0) SendMessage("", bytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки гифки: " + ex.Message, "PISMO");
            }
        }

        /// <summary>Выбор и загрузка своей аватарки (с уменьшением до 256px).</summary>
        private void UploadMyAvatar()
        {
            try
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Выберите аватар",
                    Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp"
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                byte[] data;
                using (var src = (Bitmap)Image.FromFile(dlg.FileName))
                {
                    // Квадрат по центру + уменьшение до 256 — компактно для БД.
                    int side = Math.Min(src.Width, src.Height);
                    int sx = (src.Width - side) / 2, sy = (src.Height - side) / 2;
                    int target = Math.Min(256, side);
                    using var dst = new Bitmap(target, target);
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, target, target),
                            new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                    }
                    using var ms = new MemoryStream();
                    dst.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    data = ms.ToArray();
                }

                int myId = UserSession.EffectiveId;
                if (AvatarStore.SaveMyAvatar(myId, data))
                {
                    InvalidateAvatarFor(myId);
                    MessageBox.Show("Аватар обновлён.", "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("Не удалось сохранить аватар. Выполнена ли миграция avatar_data?",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки аватара: " + ex.Message, "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── Polling: таймер на 2.5 с ───────────────────────────────────
        private void SetupPolling()
        {
            // Реальное время обеспечивает WebSocket. Таймер — ФОЛБЭК: опрашивает БД
            // ТОЛЬКО когда WS не подключён (иначе тик почти бесплатный — проверка флага
            // и выход). Так нет постоянного лага при живом WS, но сообщения доходят и
            // при обрыве WS. Плюс ручная кнопка ↻.
            _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _pollTimer.Tick += PollTick;
            _pollTimer.Start();

            // Заявки в друзья не приходят по WS — проверяем отдельным лёгким
            // таймером (один COUNT раз в 10 с) независимо от состояния WS.
            var reqTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            reqTimer.Tick += (s, e) =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    int cnt;
                    try { cnt = FriendsRepository.CountIncoming(UserSession.EffectiveId); }
                    catch { return; }
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => ApplyFriendRequests(cnt))); } catch { }
                });
            };
            reqTimer.Start();
            FormClosed += (s, e) => { try { reqTimer.Stop(); reqTimer.Dispose(); } catch { } };
        }

        private void PollTick(object sender, EventArgs e)
        {
            // Логика доставки:
            //   • WS ПОДКЛЮЧЁН  → опрос ВЫКЛЮЧЕН (таймерный тик сразу выходит) —
            //     реальное время полностью на WebSocket, никакого лага.
            //   • WS НЕ подключён → опрос раз в 3с забирает сообщения из БД.
            //   • Ручное обновление (sender==null) работает всегда.
            // Состояние WS проверяется на каждом тике (и при запуске тоже), так что
            // переключение автоматическое: WS отвалился — опрос включился, поднялся —
            // выключился.
            // РАНЬШЕ при «здоровом» WS тик выходил СРАЗУ — и если WS по факту не
            // доставлял (сокет открыт, но сервер не шлёт события; при старом ws-server
            // pong не приходит никогда, и «здоровьем» считается просто открытый сокет),
            // уведомления не приходили вовсе — только по кнопке ↻, которая идёт мимо
            // этой проверки. Теперь при здоровом WS опрос не выключается, а разряжается:
            // раз в ~9 секунд проверяем непрочитанные и шлём пуши, а открытый чат не
            // трогаем — его перезагружает сам WS.
            bool wsOk = sender != null && WebSocketSignalingClient.Instance.IsHealthy;
            if (wsOk)
            {
                if (++_wsIdleTicks < 3) return;   // 3 тика по 3с ≈ раз в 9с
                _wsIdleTicks = 0;
            }
            else _wsIdleTicks = 0;

            if (_pollBusy) return;
            _pollBusy = true;

            // Перезагрузку открытого чата (LoadMessages — запрос к БД на UI-потоке)
            // на таймерном тике делаем ТОЛЬКО если реально пришло новое сообщение
            // (дешёвая фоновая проверка COUNT). Так и лага «раз в 3 секунды» нет
            // (обычно перезагрузки не происходит), и сообщения доходят при обрыве WS.
            // Ручное обновление (sender==null) перезагружает всегда.
            bool forced = sender == null;
            int grp = _currentGroupId;
            int dm = _currentChatPartnerId;

            // id видимых карточек собираем на UI-потоке (потоконебезопасно иначе).
            var ids = new List<int>();
            try { foreach (var p in _userPanels) if (p.Tag is int uid) ids.Add(uid); } catch { }

            // Запросы непрочитанных/присутствия/проверка нового — в фоне (не вешаем UI).
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var unread = ReadUnreadCounts();
                    var groupNew = ReadGroupNew();
                    var presence = ReadPresence(ids);
                    int friendReq = -1;
                    try { friendReq = FriendsRepository.CountIncoming(UserSession.EffectiveId); } catch { }

                    // Дёшево смотрим, изменилось ли число сообщений в открытом чате.
                    bool openChanged = false;
                    try
                    {
                        if (grp >= 0) { int c = GetGroupMsgCount(); openChanged = c != _lastOpenSig; _lastOpenSig = c; }
                        else if (dm >= 0) { int c = GetMsgCount(); openChanged = c != _lastOpenSig; _lastOpenSig = c; }
                    }
                    catch { }

                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if ((forced || (openChanged && !wsOk)) && !OnServerView)
                            {
                                if (_currentGroupId >= 0) LoadGroupMessages();
                                else if (_currentChatPartnerId >= 0) LoadMessages();
                            }
                            if (unread != null) ApplyUnreadAndNotify(unread);
                            if (groupNew != null) ApplyGroupNotify(groupNew);
                            ApplyPresence(presence);
                            if (friendReq >= 0) ApplyFriendRequests(friendReq);
                        }
                        catch { }
                    }));
                }
                catch { }
                finally { _pollBusy = false; }
            });
        }

        // Применяет статусы присутствия и перерисовывает карточки ТОЛЬКО при изменении.
        private void ApplyPresence(Dictionary<int, int> fresh)
        {
            if (fresh == null) return;
            bool changed = fresh.Count != _presence.Count;
            if (!changed)
                foreach (var kv in fresh)
                    if (!_presence.TryGetValue(kv.Key, out int v) || v != kv.Value) { changed = true; break; }
            if (!changed) return;
            _presence.Clear();
            foreach (var kv in fresh) _presence[kv.Key] = kv.Value;
            InvalidateCardAvatars();
        }

        private int GetGroupMsgCount()
        {
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM group_messages WHERE group_id=@g", conn);
            cmd.Parameters.AddWithValue("@g", _currentGroupId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        private int GetMsgCount()
        {
            int myId = UserSession.EffectiveId;
            using var conn = DBHelper.OpenConnection();
            using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM messages WHERE (sender_id=@me AND receiver_id=@th) OR (sender_id=@th AND receiver_id=@me)",
                conn);
            cmd.Parameters.AddWithValue("@me", myId);
            cmd.Parameters.AddWithValue("@th", _currentChatPartnerId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // ── Чтение непрочитанных (DB, выполняется в фоне) ─────────────
        private Dictionary<int, int> ReadUnreadCounts()
        {
            int myId = UserSession.EffectiveId;
            var current = new Dictionary<int, int>();
            try
            {
                // Один запрос: непрочитанные по отправителям, СРАЗУ исключая блокировки
                // (раньше на каждого отправителя открывалось по 2 соединения к удалённой
                // БД — это и был периодический фриз и спам потоков).
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT m.sender_id, COUNT(*) AS cnt FROM messages m " +
                    "WHERE m.receiver_id=@me AND m.is_read=0 " +
                    "AND NOT EXISTS (SELECT 1 FROM user_blocks b WHERE " +
                    "   (b.blocker_id=@me AND b.blocked_id=m.sender_id) OR " +
                    "   (b.blocker_id=m.sender_id AND b.blocked_id=@me)) " +
                    "GROUP BY m.sender_id", conn);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    current[Convert.ToInt32(r["sender_id"])] = Convert.ToInt32(r["cnt"]);
            }
            catch { return null; }
            return current;
        }

        // Максимальный id чужого сообщения по каждой группе, где я состою.
        // Служит для пуша из трея при новом сообщении в групповом чате
        // (у групп нет per-user метки прочтения — базовую точку держим в памяти).
        private readonly Dictionary<int, (int maxId, string name)> _prevGroupMax = new();
        private bool _groupMaxInit;

        private Dictionary<int, (int maxId, string name)> ReadGroupNew()
        {
            int myId = UserSession.EffectiveId;
            var current = new Dictionary<int, (int, string)>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT gc.id, gc.name, COALESCE(MAX(gm.id),0) AS max_id " +
                    "FROM group_chats gc " +
                    "JOIN group_members mem ON mem.group_id = gc.id AND mem.user_id = @me " +
                    "LEFT JOIN group_messages gm ON gm.group_id = gc.id AND gm.sender_id <> @me AND gm.is_deleted = 0 " +
                    "GROUP BY gc.id, gc.name", conn);
                cmd.Parameters.AddWithValue("@me", myId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    current[Convert.ToInt32(r["id"])] =
                        (Convert.ToInt32(r["max_id"]), r["name"]?.ToString() ?? "группа");
            }
            catch { return null; }
            return current;
        }

        // Пуш из трея при новом сообщении в группе (кроме открытой группы).
        // Первый вызов только фиксирует базовую точку — без уведомления.
        private void ApplyGroupNotify(Dictionary<int, (int maxId, string name)> current)
        {
            if (current == null) return;
            if (_groupMaxInit)
            {
                int totalNew = 0; string lastName = null;
                foreach (var kv in current)
                {
                    if (kv.Key == _currentGroupId) continue;
                    _prevGroupMax.TryGetValue(kv.Key, out var prev);
                    if (kv.Value.maxId > prev.maxId && prev.maxId >= 0)
                    {
                        totalNew++; lastName = kv.Value.name;
                    }
                }
                if (totalNew > 0)
                {
                    try { Sounds.Message(); } catch { }
                    string body = totalNew == 1
                        ? $"Новое сообщение в группе «{lastName}»"
                        : $"Новые сообщения в {totalNew} {RuPlural(totalNew, "группе", "группах", "группах")}";
                    PushNotify("PISMO — сообщение", body);
                    if (!this.ContainsFocus) { try { FlashWindow(this.Handle, true); } catch { } }
                }
            }
            _prevGroupMax.Clear();
            foreach (var kv in current) _prevGroupMax[kv.Key] = kv.Value;
            _groupMaxInit = true;
        }

        // ── Применение бейджей + уведомления (UI-поток) ───────────────
        private void ApplyUnreadAndNotify(Dictionary<int, int> current)
        {
            // Уведомления при росте счётчика: собираем ВСЕХ отправителей, у кого
            // за этот тик прибавились сообщения, и показываем ОДНО агрегированное
            // уведомление (имя+кол-во для одного, кол-во людей+сообщений для нескольких).
            var grew = new List<(int sid, int delta)>();
            foreach (var kv in current)
            {
                int sid = kv.Key;
                int cnt = kv.Value;
                _prevUnread.TryGetValue(sid, out int prev);
                // Когда открыт встроенный вид сервера, ЛС-чат не виден — уведомляем
                // обо ВСЕХ входящих ЛС, включая «текущего» собеседника.
                int openPartner = OnServerView ? -1 : _currentChatPartnerId;
                if (cnt > prev && sid != openPartner)
                    grew.Add((sid, cnt - prev));
            }
            if (grew.Count > 0) ShowAggregatedNotification(grew);

            // Если непрочитанные не изменились с прошлого тика — НЕ трогаем UI вообще
            // (раньше каждые 2.5 c пересоздавались шрифты и перекладывались карточки —
            // отсюда периодический микро-фриз).
            bool changed = current.Count != _prevUnread.Count;
            if (!changed)
                foreach (var kv in current)
                    if (!_prevUnread.TryGetValue(kv.Key, out int pv) || pv != kv.Value) { changed = true; break; }

            _prevUnread.Clear();
            foreach (var kv in current) _prevUnread[kv.Key] = kv.Value;

            if (!changed) return;

            UpdateBadgesOnCards(current);

            int totalUnread = current.Values.Sum();
            string title = totalUnread > 0 ? $"● PISMO ({totalUnread} новых)" : "PISMO — Мессенджер";
            if (this.Text != title)
            {
                this.Text = title;
                _trayIcon.Text = title.Length > 63 ? title[..63] : title;
            }
        }

        /// <summary>Бейдж на кнопке «Друзья» + уведомление при НОВОЙ входящей
        /// заявке (звук, всплывашка в трее, мигание окна — как у сообщений).</summary>
        private void ApplyFriendRequests(int cnt)
        {
            if (_prevFriendReq >= 0 && cnt > _prevFriendReq)
            {
                try { Sounds.Message(); } catch { }
                PushNotify("PISMO — заявка в друзья",
                    "Вам отправили заявку в друзья. Откройте «Друзья» → «Ожидание».");
                if (!this.ContainsFocus) FlashWindow(this.Handle, true);
            }
            if (cnt != _prevFriendReq && _friendsBadge != null && !_friendsBadge.IsDisposed)
            {
                _friendsBadge.Text = cnt > 9 ? "9+" : cnt.ToString();
                _friendsBadge.Visible = cnt > 0;
            }
            _prevFriendReq = cnt;
        }

        /// <summary>Единая точка показа пуш-уведомления из трея.
        /// ShowBalloonTip МОЛЧА ничего не показывает (а в части случаев бросает
        /// исключение), если у иконки не задан Icon или она не Visible. Раньше часть
        /// уведомлений просто пропускалась по условию `_trayIcon.Icon != null` —
        /// пользователь слышал звук, но всплывашки не было. Теперь перед показом
        /// восстанавливаем и иконку, и видимость.</summary>
        internal void PushNotify(string title, string body)
        {
            try
            {
                if (_trayIcon == null) return;
                if (_trayIcon.Icon == null)
                {
                    try
                    {
                        var icoPath = System.IO.Path.Combine(
                            AppDomain.CurrentDomain.BaseDirectory, "pismo.ico");
                        _trayIcon.Icon = System.IO.File.Exists(icoPath)
                            ? new System.Drawing.Icon(icoPath)
                            : System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    }
                    catch { }
                }
                if (!_trayIcon.Visible) _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(4000, title, body, ToolTipIcon.Info);
            }
            catch { }
        }

        /// <summary>Одно уведомление на все пришедшие за тик сообщения:
        ///  • от одного пользователя → «Имя: N новых сообщений»;
        ///  • от нескольких → «K пользователей · M сообщений».</summary>
        private void ShowAggregatedNotification(List<(int sid, int delta)> grew)
        {
            try { Sounds.Message(); } catch { }

            int totalMsgs = grew.Sum(g => g.delta);
            string msgWord = RuPlural(totalMsgs, "сообщение", "сообщения", "сообщений");
            string body;

            // Имена в уведомлении подрезаем: длинные ФИО превращали всплывашку в
            // простыню, а Windows всё равно обрезает текст на своё усмотрение.
            string Who(int sid)
            {
                string n = GetNameFromCards(sid);
                return n.Length > 22 ? n[..21] + "…" : n;
            }

            if (grew.Count == 1)
            {
                // ОДИН отправитель — всегда одна и та же форма: имя, количество и тип
                // (сообщение/фото/файл/кружок…). Раньше форма «прыгала»: то «Имя: N
                // новых сообщений», то «Имя: 🖼 Фото (+2)», а без карточки в списке
                // вместо имени уходило «Пользователь #21».
                body = $"{Who(grew[0].sid)}: {totalMsgs} {msgWord} · {LatestUnreadKind(grew[0].sid)}";
            }
            else if (grew.Count <= 3)
            {
                // 2–3 отправителя — перечисляем имена и общее количество.
                var names = grew.Select(g => Who(g.sid)).ToList();
                string who = string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];
                body = $"{who} оставили вам {totalMsgs} {msgWord}";
            }
            else
            {
                // Больше трёх — только количество людей и общее количество сообщений.
                body = $"{grew.Count} {RuPlural(grew.Count, "пользователь", "пользователя", "пользователей")}"
                     + $" оставили вам {totalMsgs} {msgWord}";
            }

            PushNotify("PISMO — новые сообщения", body);

            if (!this.ContainsFocus)
                FlashWindow(this.Handle, true);
        }

        /// <summary>Тип последнего непрочитанного сообщения от отправителя для
        /// осмысленного текста уведомления (гс/гиф/файл/кружок/фото). Для обычного
        /// текста возвращает "" (тогда показываем счётчик сообщений).</summary>
        private string LatestUnreadKind(int sid)
        {
            try
            {
                int me = UserSession.EffectiveId;
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT text, (audio_data IS NOT NULL) AS a, (video_data IS NOT NULL) AS v, " +
                    "(image_data IS NOT NULL) AS i, (file_data IS NOT NULL) AS f, file_name " +
                    "FROM messages WHERE sender_id=@s AND receiver_id=@me AND is_read=0 " +
                    "ORDER BY id DESC LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@s", sid);
                cmd.Parameters.AddWithValue("@me", me);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return "💬 Текст";   // форма едина даже если строку не нашли
                bool a = r["a"] != DBNull.Value && Convert.ToInt32(r["a"]) == 1;
                bool v = r["v"] != DBNull.Value && Convert.ToInt32(r["v"]) == 1;
                bool i = r["i"] != DBNull.Value && Convert.ToInt32(r["i"]) == 1;
                bool f = r["f"] != DBNull.Value && Convert.ToInt32(r["f"]) == 1;
                string txt = "";
                try { txt = Crypto.Dec(r["text"] == DBNull.Value ? "" : r["text"].ToString()); } catch { }
                if (a) return "🎤 Голосовое";
                if (v) return "⭕ Кружок";
                if (i) return txt.StartsWith("gif:", StringComparison.OrdinalIgnoreCase) ? "🎞 GIF" : "🖼 Фото";
                if (f) return "📎 Файл";
                if (txt.StartsWith("gif:", StringComparison.OrdinalIgnoreCase)) return "🎞 GIF";
                return "💬 Текст";   // обычный текст тоже описываем; слово «сообщение» не дублируем
            }
            catch { return "💬 Текст"; }
        }

        /// <summary>Русское склонение по числу: 1 книга / 2 книги / 5 книг.</summary>
        private static string RuPlural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            if (m10 == 1 && m100 != 11) return one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return few;
            return many;
        }

        private readonly Dictionary<int, string> _nameCache = new();

        /// <summary>Имя отправителя для уведомлений. Сначала берём с карточки в списке,
        /// а если карточки нет (человек не в списке чатов, режим «за пользователя»,
        /// список ещё не построен) — дочитываем из БД и кешируем. Раньше в таком случае
        /// в уведомление уходило «Пользователь #21».</summary>
        private string GetNameFromCards(int uid)
        {
            foreach (var p in _userPanels)
            {
                if (p.Tag is int id && id == uid)
                {
                    foreach (Control c in p.Controls)
                        if (c is Label lbl && lbl.Font.Bold && lbl.ForeColor == Color.FromArgb(220, 221, 222))
                        {
                            string t = (lbl.Text ?? "").Replace("📌 ", "").Trim();
                            if (t.Length > 0) { _nameCache[uid] = t; return t; }
                        }
                }
            }
            if (_nameCache.TryGetValue(uid, out var cached)) return cached;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT TRIM(CONCAT(COALESCE(Name,''),' ',COALESCE(Surname,''))) AS fio, login " +
                    "FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    string fio = r["fio"] == DBNull.Value ? "" : r["fio"].ToString().Trim();
                    string login = r["login"] == DBNull.Value ? "" : r["login"].ToString().Trim();
                    string nm = fio.Length > 0 ? fio : login;
                    if (nm.Length > 0) { _nameCache[uid] = nm; return nm; }
                }
            }
            catch { }
            return $"Пользователь #{uid}";
        }

        private void UpdateBadgesOnCards(Dictionary<int, int> unread)
        {
            foreach (var pnl in _userPanels)
            {
                if (pnl.Tag is not int uid) continue;
                unread.TryGetValue(uid, out int cnt);

                Label badge = null;
                foreach (Control c in pnl.Controls)
                    if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                    { badge = lb; break; }

                if (cnt > 0 && badge == null)
                {
                    badge = MakeBadge(cnt, pnl.Width);   // реальное число, ширина под цифры
                    pnl.Controls.Add(badge);
                    badge.BringToFront();
                }
                else if (cnt > 0 && badge != null)
                {
                    // Реальное число + подгон ширины, иначе «73» не влезало в 22px.
                    badge.Text = cnt > 999 ? "999+" : cnt.ToString();
                    int bw = badge.Text.Length <= 1 ? 22 : 22 + (badge.Text.Length - 1) * 8;
                    badge.Size = new Size(bw, 18);
                    badge.Location = new Point(pnl.Width - 12 - bw, 22);
                }
                else if (cnt == 0 && badge != null)
                {
                    pnl.Controls.Remove(badge);
                }

                var wantFont = cnt > 0 ? _cardFontBold : _cardFontNormal;
                foreach (Control c in pnl.Controls)
                    if (c is Label lbl && lbl.Font.Size >= 9 && !ReferenceEquals(lbl.Font, wantFont))
                        lbl.Font = wantFont; // кэшированные шрифты — без аллокаций каждый тик
            }
        }

        // Кэшированные шрифты карточек (раньше создавались заново на каждом тике).
        private static readonly Font _cardFontBold = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        private static readonly Font _cardFontNormal = new Font("Segoe UI", 10f);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        // ════════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА САЙДБАРА
        // ════════════════════════════════════════════════════════════════
        private void LoadConversations()
        {
            try { ChatScroll.Attach(pnlUserList); ChatScroll.KillHorizontal(pnlSidebar); } catch { }   // тонкий верт. + без гор. в сайдбаре
            pnlUserList.Controls.Clear();
            _userPanels.Clear();
            _groupPanels.Clear();

            int myId = UserSession.EffectiveId;
            lblSidebarTitle.Text = UserSession.IsImpersonating
                ? $"💬 За: {UserSession.EffectiveName}"
                : "Личные сообщения";

            FriendsRepository.EnsureTable();

            AddFriendsHeaderButton(myId);

            LoadGroups();

            try
            {
                using var conn = DBHelper.OpenConnection();
                string sql = @"
                    SELECT u.id, u.Name, u.Surname, u.login,
                           MAX(m.created_at) AS last_time,
                           (SELECT m2.text FROM messages m2
                            WHERE (m2.sender_id = @me AND m2.receiver_id = u.id)
                               OR (m2.sender_id = u.id AND m2.receiver_id = @me)
                            ORDER BY m2.created_at DESC LIMIT 1) AS last_msg,
                           SUM(CASE WHEN m.sender_id=u.id
                                     AND m.receiver_id=@me
                                     AND m.is_read=0 THEN 1 ELSE 0 END) AS unread
                    FROM users u
                    LEFT JOIN messages m
                           ON (m.sender_id=@me AND m.receiver_id=u.id)
                           OR (m.sender_id=u.id AND m.receiver_id=@me)
                    WHERE u.id <> @me
                      AND ( EXISTS (SELECT 1 FROM friends f
                                    WHERE " + FriendsRepository.AcceptedPredicate("f") + @" AND ((f.user_id=@me AND f.friend_id=u.id)
                                                       OR (f.user_id=u.id AND f.friend_id=@me)))
                         OR EXISTS (SELECT 1 FROM messages mm
                                    WHERE (mm.sender_id=@me AND mm.receiver_id=u.id)
                                       OR (mm.sender_id=u.id AND mm.receiver_id=@me)) )
                    GROUP BY u.id, u.Name, u.Surname, u.login
                    ORDER BY last_time DESC, u.Name ASC";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", myId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                // Пометка «не в друзьях» на карточках написавших не-друзей.
                var friendIds = FriendsRepository.AcceptedIds(myId);

                // Закреплённые чаты (2.1) — к ВЕРХУ списка ЛС (группы выше и не
                // трогаются). Внутри закреплённых и обычных сохраняется порядок
                // SQL (по свежести переписки). Закрепы хранятся локально.
                var ordered = new List<DataRow>();
                foreach (DataRow row in dt.Rows)
                    if (ChatPins.IsPinned(Convert.ToInt32(row["id"]))) ordered.Add(row);
                foreach (DataRow row in dt.Rows)
                    if (!ChatPins.IsPinned(Convert.ToInt32(row["id"]))) ordered.Add(row);

                foreach (DataRow row in ordered)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string name = BuildName(row["Name"], row["Surname"], row["login"]);
                    string lastMsg = row["last_msg"] == DBNull.Value ? "" : Crypto.Dec(row["last_msg"].ToString());
                    int unread = row["unread"] == DBNull.Value ? 0 : Convert.ToInt32(row["unread"]);

                    if (!friendIds.Contains(uid))
                        lastMsg = "🚫 не в друзьях" + (string.IsNullOrEmpty(lastMsg) ? "" : " · " + lastMsg);

                    AddUserCard(uid, name, lastMsg, unread, pinned: ChatPins.IsPinned(uid));
                }
                if (_convSearch != null) FilterConversations(_convSearch.Text);
                try { pnlUserList_Resize(null, null); } catch { }   // подогнать ширину карточек → без гор.скролла
                try { ChatScroll.ApplyDarkScrollbar(pnlUserList); } catch { }   // тёмная полоса и в списке контактов
                try { BeginInvoke(new Action(() => ChatScroll.ApplyDarkScrollbar(pnlUserList))); } catch { }
                try { PresenceTick(); } catch { } // разово обновить статусы под список
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки диалогов: " + ex.Message);
            }
        }

        /// <summary>Окно поиска и добавления друзей; после изменений — перезагрузка списка.</summary>
        /// <summary>Кнопка «👥 Друзья» вверху списка (и у обычных пользователей, и у
        /// админа) — открывает Discord-подобное окно (В сети / Все / Ожидание /
        /// Добавить). При входящих заявках горит красный бейдж с числом.</summary>
        private void AddFriendsHeaderButton(int myId)
        {
            int pendingCount = 0;
            try { pendingCount = FriendsRepository.CountIncoming(myId); } catch { }
            var btnAddFriend = new Button
            {
                Text = "👥  Друзья",
                Width = CardWidth,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 165, 93),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Margin = new Padding(6, 4, 6, 6)
            };
            btnAddFriend.FlatAppearance.BorderSize = 0;
            btnAddFriend.Click += (s, e) => OpenAddFriend();
            RoundCorners(btnAddFriend, 8);   // скруглённые углы (как в Discord)

            _friendsBadge = new Label
            {
                Text = pendingCount > 9 ? "9+" : pendingCount.ToString(),
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(240, 71, 71),
                Size = new Size(22, 18),
                Location = new Point(CardWidth - 32, 8),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = pendingCount > 0,
                Cursor = Cursors.Hand
            };
            _friendsBadge.Click += (s, e) => OpenAddFriend();
            btnAddFriend.Controls.Add(_friendsBadge);
            _prevFriendReq = pendingCount;

            pnlUserList.Controls.Add(btnAddFriend);
        }

        private void OpenAddFriend()
        {
            using var f = new FriendsAddForm(UserSession.EffectiveId);
            f.ShowDialog(this);
            if (f.Changed) LoadConversations();
            if (f.OpenChatWith is int uid)
            {
                string nm = BuildNameById(uid);
                OpenChat(uid, nm);
            }
        }

        /// <summary>Имя пользователя по id (для открытия чата из окна «Друзья»).</summary>
        private string BuildNameById(int uid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT Name, Surname, login FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                using var r = cmd.ExecuteReader();
                if (r.Read()) return BuildName(r["Name"], r["Surname"], r["login"]);
            }
            catch { }
            return "Пользователь #" + uid;
        }

        private void LoadAllUsersForAdmin()
        {
            pnlUserList.Controls.Clear();
            _userPanels.Clear();
            _groupPanels.Clear();
            lblSidebarTitle.Text = "Все пользователи";

            AddFriendsHeaderButton(UserSession.UserId);   // окно «Друзья» есть и у админа

            LoadGroups();

            var lblHint = new Label
            {
                Text = "ЛКМ — написать  •  ПКМ — войти за пользователя",
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(90, 93, 102),
                AutoSize = false,
                Width = CardWidth,   // как у карточек: с резервом под вертикальную полосу
                Height = 30,
                Padding = new Padding(8, 6, 0, 0)
            };
            pnlUserList.Controls.Add(lblHint);

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "SELECT id, Name, Surname, login, role FROM users ORDER BY Name";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", UserSession.UserId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                // Закреплённые чаты — к верху (ниже групп), как в обычном списке.
                var ordered = new List<DataRow>();
                foreach (DataRow row in dt.Rows)
                    if (ChatPins.IsPinned(Convert.ToInt32(row["id"]))) ordered.Add(row);
                foreach (DataRow row in dt.Rows)
                    if (!ChatPins.IsPinned(Convert.ToInt32(row["id"]))) ordered.Add(row);

                foreach (DataRow row in ordered)
                {
                    int uid = Convert.ToInt32(row["id"]);
                    string name = BuildName(row["Name"], row["Surname"], row["login"]);
                    string role = row["role"].ToString();
                    AddAdminUserCard(uid, name, role);
                }
                try { pnlUserList_Resize(null, null); } catch { }
                try { ChatScroll.ApplyDarkScrollbar(pnlUserList); } catch { }   // тёмная полоса в списке пользователей
                try { BeginInvoke(new Action(() => ChatScroll.ApplyDarkScrollbar(pnlUserList))); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ГРУППОВЫЕ ЧАТЫ — ЗАГРУЗКА СПИСКА
        // ════════════════════════════════════════════════════════════════
        private void LoadGroups()
        {
            int myId = UserSession.EffectiveId;

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = @"
                    SELECT gc.id, gc.name, gc.avatar_color,
                           (SELECT gm2.text FROM group_messages gm2
                            WHERE gm2.group_id = gc.id
                            ORDER BY gm2.created_at DESC LIMIT 1) AS last_msg,
                           (SELECT MAX(gm3.created_at) FROM group_messages gm3
                            WHERE gm3.group_id = gc.id) AS last_time,
                           (SELECT COUNT(*) FROM group_members gmem2 WHERE gmem2.group_id = gc.id) AS member_count
                    FROM group_chats gc
                    JOIN group_members gmem ON gmem.group_id = gc.id AND gmem.user_id = @me
                    ORDER BY last_time DESC, gc.name ASC";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@me", myId);

                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);

                if (dt.Rows.Count == 0) return;

                var lblHeader = new Label
                {
                    Text = "ГРУППЫ",
                    Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(114, 118, 125),
                    AutoSize = false,
                    Width = CardWidth,
                    Height = 24,
                    Padding = new Padding(10, 8, 0, 0)
                };
                pnlUserList.Controls.Add(lblHeader);

                foreach (DataRow row in dt.Rows)
                {
                    int gid = Convert.ToInt32(row["id"]);
                    string name = row["name"].ToString();
                    string lastMsg = row["last_msg"] == DBNull.Value ? "" : Crypto.Dec(row["last_msg"].ToString());
                    int members = Convert.ToInt32(row["member_count"]);
                    string colorHex = row["avatar_color"] == DBNull.Value ? "#5865F2" : row["avatar_color"].ToString();

                    AddGroupCard(gid, name, lastMsg, members, colorHex);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка загрузки групп: " + ex.Message);
            }
        }

        private void AddGroupCard(int gid, string name, string lastMsg, int memberCount, string colorHex)
        {
            Color avatarColor;
            try { avatarColor = ColorTranslator.FromHtml(colorHex); }
            catch { avatarColor = Color.FromArgb(88, 101, 242); }

            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 62,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = gid
            };

            var avatar = new Panel { Size = new Size(38, 38), Location = new Point(10, 12) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(new SolidBrush(avatarColor),
                    0, 0, avatar.Width - 1, avatar.Height - 1);
                string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "#";
                using var f = new Font("Segoe UI Black", 14f, FontStyle.Bold);
                var sz = e.Graphics.MeasureString(letter, f);
                e.Graphics.DrawString(letter, f, Brushes.White,
                    (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
            };

            var lblName = new Label
            {
                Text = "👥 " + name,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 10),
                Size = new Size(pnl.Width - 90, 20),
                AutoEllipsis = true
            };

            string preview = string.IsNullOrWhiteSpace(lastMsg)
                ? $"{memberCount} участник(ов)"
                : (lastMsg.Length > 42 ? lastMsg[..42] + "…" : lastMsg);

            var lblLast = new Label
            {
                Name = "lastPreview",   // чтобы обновлять превью локально (без перезагрузки списка)
                Text = preview,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 32),
                Size = new Size(pnl.Width - 90, 18),
                AutoEllipsis = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);
            pnl.Controls.Add(lblLast);

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                56, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentGroupId == gid
                    ? Theme.Map(Color.FromArgb(65, 68, 75)) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);
            // ЛКМ открывает группу; ПКМ — только меню (иначе Panel/Label вызывают
            // Click и на правую кнопку, и чат открывался вместе с меню).
            pnl.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenGroup(gid, name); };

            // Контекстное меню: участники / добавление / выход из группы
            var ctxMenu = new ContextMenuStrip();
            ctxMenu.Items.Add("👥 Участники группы", null, (s, ev) => ShowGroupMembers(gid, name));
            ctxMenu.Items.Add("➕ Добавить участников", null, (s, ev) => QuickAddMembers(gid, name));
            ctxMenu.Items.Add(new ToolStripSeparator());
            ctxMenu.Items.Add("🚪 Покинуть группу", null, (s, ev) => LeaveGroup(gid, name));
            pnl.ContextMenuStrip = ctxMenu;

            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenGroup(gid, name); };
                c.ContextMenuStrip = ctxMenu;
            }

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _groupPanels.Add(pnl);
        }

        private void QuickAddMembers(int gid, string groupName)
        {
            using var dlg = new AddGroupMembersForm(gid);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.AddedCount > 0)
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                if (_currentGroupId == gid)
                    LoadGroupMessages();
            }
        }

        private void ShowGroupMembers(int gid, string groupName)
        {
            using var dlg = new GroupMembersForm(gid, groupName);
            dlg.ShowDialog(this);

            if (dlg.Changed)
            {
                // Если меня самого исключили или состав изменился — перезагружаем сайдбар
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                if (_currentGroupId == gid)
                {
                    // Проверим, остался ли я в группе
                    if (!IsStillGroupMember(gid))
                        ClearChat();
                    else
                        LoadGroupMessages();
                }
            }
        }

        private bool IsStillGroupMember(int gid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@g", gid);
                cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch { return true; }
        }

        private void LeaveGroup(int gid, string groupName)
        {
            var confirm = MessageBox.Show(
                $"Покинуть группу «{groupName}»?",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                cmd.Parameters.AddWithValue("@g", gid);
                cmd.Parameters.AddWithValue("@u", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();

                if (_currentGroupId == gid) ClearChat();

                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
            }
        }

        private void btnNewGroup_Click(object sender, EventArgs e)
        {
            using var dlg = new CreateGroupForm();
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.CreatedGroupId > 0)
            {
                if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                    LoadAllUsersForAdmin();
                else
                    LoadConversations();

                OpenGroup(dlg.CreatedGroupId, dlg.CreatedGroupName);
            }
        }


        // Ширина карточки — от РЕАЛЬНОЙ клиентской области списка (без верт.
        // скроллбара) минус паддинги/отступы, иначе карточки шире панели →
        // горизонтальный скролл и «наползание» на края.
        private int CardWidth
        {
            get
            {
                int w = pnlUserList != null && pnlUserList.Width > 0
                    ? pnlUserList.Width
                    : pnlSidebar.Width;
                // От СТАБИЛЬНОЙ Width резервируем Padding панели (8) + Margin
                // карточки (6) + ширину вертикальной полосы — тогда карточка не
                // вылезает даже когда появляется вертикальный скролл.
                return Math.Max(200, w - 14 - SystemInformation.VerticalScrollBarWidth);
            }
        }

        private void AddUserCard(int uid, string name, string lastMsg, int unread, bool pinned = false)
        {
            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 62,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = uid
            };

            var avatar = new Panel { Size = new Size(38, 38), Location = new Point(10, 12) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, uid, 0, 0, avatar.Width - 1))
                {
                    e.Graphics.FillEllipse(new SolidBrush(GetAvatarColor(uid)),
                        0, 0, avatar.Width - 1, avatar.Height - 1);
                    string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 14f, FontStyle.Bold);
                    var sz = e.Graphics.MeasureString(letter, f);
                    e.Graphics.DrawString(letter, f, Brushes.White,
                        (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
                }
                DrawPresenceDot(e.Graphics, avatar.Width, avatar.Height, uid);
            };

            var lblName = new Label
            {
                // 📌 — визуальный признак закреплённого чата (2.1).
                Text = (pinned ? "📌 " : "") + name,
                Font = unread > 0
                    ? new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
                    : new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 10),
                Size = new Size(pnl.Width - 90, 20),
                AutoEllipsis = true
            };

            var lblLast = new Label
            {
                Name = "lastPreview",   // чтобы обновлять превью локально (без перезагрузки списка)
                Text = lastMsg.Length > 42 ? lastMsg[..42] + "…" : lastMsg,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 32),
                Size = new Size(pnl.Width - 90, 18),
                AutoEllipsis = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);
            pnl.Controls.Add(lblLast);

            if (unread > 0)
                pnl.Controls.Add(MakeBadge(unread, pnl.Width));

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                56, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentChatPartnerId == uid
                    ? Theme.Map(Color.FromArgb(65, 68, 75)) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);
            // ЛКМ открывает чат; ПКМ — только меню действий (Panel/Label иначе
            // вызывают Click и на правую кнопку → чат открывался вместе с меню).
            pnl.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenChat(uid, name); };

            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenChat(uid, name); };
            }

            // ВАЖНО: задействуем контекстное меню из partial (AttachConversationContextMenu)
            // чтобы пункты блок/разблок/очистка переписки заработали.
            AttachConversationContextMenu(pnl, uid, name);

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _userPanels.Add(pnl);
        }

        private void AddAdminUserCard(int uid, string name, string role)
        {
            bool isAdminCard = (uid == UserSession.UserId);
            var pnl = new Panel
            {
                Width = CardWidth,
                Height = 58,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = uid
            };

            var avatar = new Panel { Size = new Size(36, 36), Location = new Point(10, 11) };
            avatar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (!AvatarStore.DrawAvatar(e.Graphics, uid, 0, 0, avatar.Width - 1))
                {
                    var col = isAdminCard ? Color.FromArgb(88, 101, 242) : GetAvatarColor(uid);
                    e.Graphics.FillEllipse(new SolidBrush(col),
                        0, 0, avatar.Width - 1, avatar.Height - 1);
                    string letter = name.Length > 0 ? name[0].ToString().ToUpper() : "?";
                    using var f = new Font("Segoe UI Black", 12f, FontStyle.Bold);
                    var sz = e.Graphics.MeasureString(letter, f);
                    e.Graphics.DrawString(letter, f, Brushes.White,
                        (avatar.Width - sz.Width) / 2, (avatar.Height - sz.Height) / 2);
                }
                DrawPresenceDot(e.Graphics, avatar.Width, avatar.Height, uid);
            };

            var lblName = new Label
            {
                // 📌 — закреплённый чат (2.1.1): работает и в админском списке.
                Text = (ChatPins.IsPinned(uid) ? "📌 " : "") + (isAdminCard ? $"{name} (Вы)" : name),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(54, 18),  // по центру по вертикали (нет подписи роли)
                Size = new Size(pnl.Width - 60, 22),
                AutoSize = true
            };

            pnl.Controls.Add(avatar);
            pnl.Controls.Add(lblName);

            void SetHover(bool on) =>
                pnl.BackColor = on || _currentChatPartnerId == uid
                    ? Theme.Map(Color.FromArgb(65, 68, 75)) : Color.Transparent;

            pnl.MouseEnter += (s, e) => SetHover(true);
            pnl.MouseLeave += (s, e) => SetHover(false);

            void OpenDirect()
            {
                if (isAdminCard) return;
                UserSession.StopImpersonating();
                lblCurrentUser.Text = UserSession.UserName;
                RemoveExitImpersonateButton();
                OpenChat(uid, name);
            }

            pnl.Click += (s, e) => OpenDirect();
            foreach (Control c in pnl.Controls)
            {
                c.MouseEnter += (s, e) => SetHover(true);
                c.MouseLeave += (s, e) => SetHover(false);
                c.Click += (s, e) => OpenDirect();
            }

            if (!isAdminCard)
            {
                var ctxMenu = new ContextMenuStrip();
                ctxMenu.Items.Add($"💬 Написать как Admin → {name}", null,
                    (s, ev) => OpenDirect());
                ctxMenu.Items.Add("👤 Профиль", null, (s, ev) =>
                {
                    using var pf = new ProfileForm(uid, readOnly: true);
                    pf.ShowDialog(this);
                });
                ctxMenu.Items.Add(new ToolStripSeparator());
                ctxMenu.Items.Add($"🚪 Войти за {name}", null,
                    (s, ev) => DoImpersonate(uid, name));

                // Закрепление чата (2.1.1): и в админском списке диалог можно
                // прижать к верху (ниже групп). Хранится локально.
                var itemPinChat = new ToolStripMenuItem(
                    ChatPins.IsPinned(uid) ? "📌 Открепить чат" : "📌 Закрепить чат");
                itemPinChat.Click += (s, ev) =>
                {
                    ChatPins.Toggle(uid);
                    try { LoadAllUsersForAdmin(); } catch { }
                };
                ctxMenu.Items.Add(itemPinChat);

                // Дополнительно: пункты блокировки/очистки переписки в админской таблице
                ctxMenu.Items.Add(new ToolStripSeparator());

                var itemBlock = new ToolStripMenuItem(
                    IsUserBlocked(UserSession.EffectiveId, uid) ? "✅ Разблокировать пользователя" : "🚫 Заблокировать пользователя");
                // Надпись пересчитываем при КАЖДОМ открытии меню — иначе после
                // блокировки пункт оставался «Заблокировать».
                ctxMenu.Opening += (s, ev) =>
                    itemBlock.Text = IsUserBlocked(UserSession.EffectiveId, uid)
                        ? "✅ Разблокировать пользователя" : "🚫 Заблокировать пользователя";
                itemBlock.Click += (s, ev) =>
                {
                    try
                    {
                        if (IsUserBlocked(UserSession.EffectiveId, uid))
                        {
                            UnblockUser(UserSession.EffectiveId, uid);
                            MessageBox.Show($"Пользователь {name} разблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            BlockUser(UserSession.EffectiveId, uid);
                            MessageBox.Show($"Пользователь {name} заблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                ctxMenu.Items.Add(itemBlock);

                var itemClear = new ToolStripMenuItem("🗑 Очистить переписку");
                itemClear.Click += (s, ev) => DeleteConversationWithPartner(uid, name);
                ctxMenu.Items.Add(itemClear);

                pnl.ContextMenuStrip = ctxMenu;
                foreach (Control c in pnl.Controls)
                    c.ContextMenuStrip = ctxMenu;
            }

            pnl.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(Color.FromArgb(40, 255, 255, 255)),
                54, pnl.Height - 1, pnl.Width - 8, pnl.Height - 1);

            pnl.AccessibleName = name;
            pnlUserList.Controls.Add(pnl);
            _userPanels.Add(pnl);
        }

        /// <summary>Красный счётчик непрочитанных на карточке чата. Показывает реальное
        /// число (раньше всё, что больше 9, превращалось в «9+», да ещё и «+» не влезал
        /// в 22px — выглядело как «9» при 73 непрочитанных). Ширина подстраивается под
        /// количество цифр, правый край остаётся на месте.</summary>
        private Label MakeBadge(int count, int parentWidth)
        {
            string text = count > 999 ? "999+" : count.ToString();
            int w = text.Length <= 1 ? 22 : 22 + (text.Length - 1) * 8;
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(240, 71, 71),
                Size = new Size(w, 18),
                Location = new Point(parentWidth - 12 - w, 22),
                TextAlign = ContentAlignment.MiddleCenter
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  IMPERSONATION
        // ════════════════════════════════════════════════════════════════
        private Button _btnExitImpersonate;

        private void DoImpersonate(int uid, string name)
        {
            UserSession.ImpersonatedId = uid;
            UserSession.ImpersonatedName = name;
            lblCurrentUser.Text = $"👤 За: {name}";
            ShowExitImpersonateButton();
            ClearChat();
            LoadConversations();
        }

        private void ShowExitImpersonateButton()
        {
            RemoveExitImpersonateButton();

            _btnExitImpersonate = new Button
            {
                Text = "← Вернуться к себе",
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = Color.FromArgb(240, 71, 71),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnExitImpersonate.FlatAppearance.BorderSize = 0;
            _btnExitImpersonate.Click += (s, e) =>
            {
                UserSession.StopImpersonating();
                lblCurrentUser.Text = UserSession.UserName;
                RemoveExitImpersonateButton();
                ClearChat();
                LoadAllUsersForAdmin();
            };

            pnlSidebar.Controls.Add(_btnExitImpersonate);
            pnlSidebar.Controls.SetChildIndex(_btnExitImpersonate, 0);
        }

        private void RemoveExitImpersonateButton()
        {
            if (_btnExitImpersonate != null)
            {
                pnlSidebar.Controls.Remove(_btnExitImpersonate);
                _btnExitImpersonate.Dispose();
                _btnExitImpersonate = null;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТКРЫТИЕ ЧАТА
        // ════════════════════════════════════════════════════════════════
        private void OpenChat(int partnerId, string partnerName)
        {
            _currentChatPartnerId = partnerId;
            _currentChatPartnerName = partnerName;
            _currentGroupId = -1;
            _currentGroupName = "";
            _lastMsgCount = 0;
            // Восстанавливаем столько сообщений, сколько было долистано ранее.
            _dmLimit = MsgPageSize;   // каждое открытие чата — снова одна страница
            _dmHasMore = false;
            _dmLoadingOlder = false;
            _dmRestoreFromBottom = -1;
            EnsureDmScrollHook();

            lblChatTitle.Text = "@ " + partnerName;
            UpdateChatHeaderPresence();   // статус собеседника (в сети / бездействует / был в сети)

            foreach (var p in _userPanels)
                p.BackColor = (p.Tag is int id && id == partnerId)
                    ? Theme.Map(Color.FromArgb(65, 68, 75)) : Color.Transparent;
            foreach (var p in _groupPanels)
                p.BackColor = Color.Transparent;

            MarkAsRead(partnerId);
            LoadMessages();
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТКРЫТИЕ ГРУППОВОГО ЧАТА
        // ════════════════════════════════════════════════════════════════
        private void OpenGroup(int groupId, string groupName)
        {
            _currentGroupId = groupId;
            _currentGroupName = groupName;
            _currentChatPartnerId = -1;
            _currentChatPartnerName = "";
            _lastGroupMsgCount = 0;
            _dmLimit = MsgPageSize;   // каждое открытие группы — снова одна страница
            _dmHasMore = false;
            _dmLoadingOlder = false;
            _dmRestoreFromBottom = -1;
            EnsureDmScrollHook();

            lblChatTitle.Text = "👥 " + groupName;
            UpdateChatHeaderPresence();   // группа — статус собеседника прячется

            foreach (var p in _groupPanels)
                p.BackColor = (p.Tag is int id && id == groupId)
                    ? Theme.Map(Color.FromArgb(65, 68, 75)) : Color.Transparent;
            foreach (var p in _userPanels)
                p.BackColor = Color.Transparent;

            LoadGroupMessages();
        }

        private void LoadGroupMessages()
        {
            if (_currentGroupId < 0) return;
            int group = _currentGroupId;
            int myId = UserSession.EffectiveId;

            // 1) Мгновенно рисуем из кеша (память → диск), чтобы открытие группы
            //    было без задержек, как в личных чатах.
            if (!_groupMetaCache.TryGetValue(group, out var cachedDt))
            {
                cachedDt = MessageCache.Load(MessageCache.GroupKey(group));
                if (cachedDt != null) _groupMetaCache[group] = cachedDt;
            }
            // Из кеша рисуем ТОЛЬКО последнюю страницу: дисковый кеш хранит всю
            // долистанную историю и переживает перезапуск, поэтому раньше лимит
            // поднимался до размера кеша и группа прогружалась целиком сразу.
            if (cachedDt != null && !_dmLoadingOlder)
            {
                // См. пояснение в ЛС: переход к дате применяем только к свежей выборке.
                var savedJump = _pendingJumpDate;
                _pendingJumpDate = null;
                RenderGroupMessages(TakeLastRows(cachedDt, _dmLimit), myId, group);
                _pendingJumpDate = savedJump;
            }

            // 2) Свежие данные тянем в ФОНЕ и перерисовываем, если всё ещё в группе.
            System.Threading.Tasks.Task.Run(() =>
            {
                DataTable dt = null;
                try { dt = LoadGroupMessagesMetaOnly(group, _dmLimit); } catch { }
                if (dt == null) return;
                // Медиа страницы — одним запросом в фоне (см. пояснение в ЛС).
                try { PrefetchPageMedia(dt, isGroup: true); } catch { }
                _dmHasMore = dt.Rows.Count >= _dmLimit;
                try { MessageCache.Save(MessageCache.GroupKey(group), dt); } catch { }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_currentGroupId != group) return; // уже переключились
                        _groupMetaCache[group] = dt;
                        RenderGroupMessages(dt, myId, group);
                    }));
                }
                catch { }
            });
        }

        /// <summary>Отрисовка групповой переписки из готового DataTable (из кеша
        /// мгновенно и из фоновой подгрузки).</summary>
        private void RenderGroupMessages(DataTable dt, int myId, int group)
        {
            if (_currentGroupId != group) return;

            // Пропускаем повторную отрисовку, если та же группа и данные не изменились.
            string key = "g" + group, sig = SigOf(dt);
            if (_renderedChatKey == key && _renderedChatSig == sig) return;
            _renderedChatKey = key; _renderedChatSig = sig;
            try { _pinnedInView = PinsRepository.PinnedIds(1); } catch { _pinnedInView = null; }
            try
            {
                var ids = new List<int>();
                foreach (DataRow rr in dt.Rows) if (rr["id"] != DBNull.Value) ids.Add(Convert.ToInt32(rr["id"]));
                _reactionsInView = ReactionsRepository.ForMessages(ids, ReactionsRepository.Scope.Group, UserSession.EffectiveId);
            }
            catch { _reactionsInView = null; }

            // Прокрутку сбрасываем в начало ДО очистки и ДО SuspendLayout — пока
            // пузыри ещё на месте. У AutoScroll-панели Top отсчитывается от сдвинутого
            // начала координат: если оставить панель прокрученной, новые пузыри уедут
            // вниз на величину прокрутки и сверху появится большая пустота. На пустой
            // панели с приостановленной раскладкой сброс не срабатывает — начало
            // координат не пересчитывается, поэтому порядок важен.
            try { pnlMessages.AutoScrollPosition = new Point(0, 0); } catch { }
            ChatScroll.SuspendDraw(pnlMessages);   // заморозка отрисовки — без мигания «загрузки заново»
            pnlMessages.SuspendLayout();
            DisposeAndClear(pnlMessages);

            try
            {
                int yOffset = 10;
                string lastDate = "";

                foreach (DataRow row in dt.Rows)
                {
                    int senderId = Convert.ToInt32(row["sender_id"]);
                    bool isMine = senderId == myId;
                    string text = Crypto.Dec(row["text"].ToString());
                    string sname = row["sender_name"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(sname)) sname = row["login"].ToString();
                    DateTime dt2 = ToViewerLocal(Convert.ToDateTime(row["created_at"]));
                    string time = dt2.ToString("HH:mm");
                    string date = dt2.ToString("d MMMM yyyy",
                                        new System.Globalization.CultureInfo("ru-RU"));

                    if (date != lastDate)
                    {
                        var sep = BuildDateSeparator(date);
                        sep.Top = yOffset;
                        pnlMessages.Controls.Add(sep);
                        yOffset += sep.Height + 4;
                        lastDate = date;
                    }

                    int msgId = Convert.ToInt32(row["id"]);
                    int replyToId = row["reply_to_id"] == DBNull.Value ? 0 : Convert.ToInt32(row["reply_to_id"]);
                    bool isDeleted = Convert.ToBoolean(row["is_deleted"]);
                    bool isEdited = row["edited_at"] != DBNull.Value;
                    string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();

                    bool hasImg = row["has_img"] != DBNull.Value && Convert.ToBoolean(row["has_img"]);
                    bool hasAudio = row["has_audio"] != DBNull.Value && Convert.ToBoolean(row["has_audio"]);
                    bool hasVideo = row["has_video"] != DBNull.Value && Convert.ToBoolean(row["has_video"]);
                    bool hasFile = row["has_file"] != DBNull.Value && Convert.ToBoolean(row["has_file"]);

                    long fileSize = row.Table.Columns.Contains("file_size") && row["file_size"] != DBNull.Value
                        ? Convert.ToInt64(row["file_size"]) : -1;

                    var (img, audio, video, fileData) = LoadMediaForMessage(
                        msgId, fileName, hasImg, hasAudio, hasVideo, hasFile, isGroup: true, fileSize);

                    var bubble = BuildBubble(sname, time, text, img, audio, isMine, video,
                        fileData, fileName, msgId, isGroup: true, replyToId, isDeleted, isEdited, fileSize);
                    bubble.Top = yOffset;
                    PositionBubble(bubble, isMine);
                    bubble.Tag = isMine;
                    // Полная дата отправки — для выпадающего списка результатов поиска.
                    bubble.AccessibleDefaultActionDescription = dt2.ToString("dd.MM.yyyy HH:mm");

                    pnlMessages.Controls.Add(bubble);
                    AddSelectMark(bubble, msgId);
                    yOffset += bubble.Height + 8;
                }

                _lastGroupMsgCount = dt.Rows.Count;
                pnlMessages.ResumeLayout();
                NormalizeTopOffset(pnlMessages);   // подстраховка от «пустоты» сверху

                if (_dmRestoreFromBottom >= 0)
                {
                    pnlMessages.PerformLayout();
                    int viewport = pnlMessages.ClientSize.Height;
                    int newTop = pnlMessages.DisplayRectangle.Height - viewport - _dmRestoreFromBottom;
                    try { pnlMessages.AutoScrollPosition = new Point(0, Math.Max(0, newTop)); } catch { }
                    _dmRestoreFromBottom = -1;
                }
                else if (_pendingJumpDate == null)
                {
                    // ВАЖНО: сперва пересчитать layout, иначе диапазон прокрутки
                    // остаётся от ПРЕДЫДУЩЕГО (длинного) чата и MaxValue уводит в
                    // «пустоту» сверху над короткой перепиской.
                    pnlMessages.PerformLayout();
                    pnlMessages.AutoScrollPosition = new Point(0, int.MaxValue);
                }
                else pnlMessages.PerformLayout();   // ждём переход к дате — вниз не скидываем
                ApplyPendingJump();                 // переход выполняем ПОСЛЕ прокрутки
                _dmLoadingOlder = false;
                UpdateScrollDownButton();
                ChatScroll.ResumeDraw(pnlMessages);   // разморозка ПОСЛЕ восстановления позиции
            }
            catch (Exception ex)
            {
                pnlMessages.ResumeLayout();
                ChatScroll.ResumeDraw(pnlMessages);
                MessageBox.Show("Ошибка загрузки сообщений группы: " + ex.Message);
            }
        }

        private void SendGroupMessage(string text, byte[] imageData,
            byte[] audioData = null, byte[] videoData = null,
            byte[] fileData = null, string fileName = null)
        {
            int myId = UserSession.EffectiveId;

            if (fileData != null && fileData.LongLength > 0)
            {
                bool ok = SendFileWithProgress(isGroup: true, target: _currentGroupId, myId: myId, text: text,
                    imageData: imageData, audioData: audioData, videoData: videoData,
                    fileData: fileData, fileName: fileName);
                if (ok)
                    WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentGroupId, "group");
                else
                    return;
            }
            else
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "INSERT INTO group_messages " +
                    "(group_id, sender_id, text, image_data, audio_data, video_data, file_data, file_name) " +
                    "VALUES (@g, @s, @t, @img, @aud, @vid, @fd, @fn)";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@g", _currentGroupId);
                cmd.Parameters.AddWithValue("@s", myId);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));

                AddBlob(cmd, "@img", imageData);
                AddBlob(cmd, "@aud", audioData);
                AddBlob(cmd, "@vid", videoData);
                AddBlob(cmd, "@fd", fileData);
                cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);

                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentGroupId, "group");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки: " + ex.Message);
                return;
            }

            // Как и в личке: без полной перезагрузки списка и повторного
            // OpenGroup — только сообщения и локальное превью на карточке.
            LoadGroupMessages();
            UpdateCardPreview(_groupPanels, _currentGroupId,
                PreviewOf(text, imageData, audioData, videoData, fileName));
        }

        private void ClearChat()
        {
            _currentChatPartnerId = -1;
            _currentChatPartnerName = "";
            _currentGroupId = -1;
            _currentGroupName = "";
            _lastMsgCount = 0;
            _lastGroupMsgCount = 0;
            lblChatTitle.Text = "# Выберите диалог";
            if (_lblChatPresence != null) _lblChatPresence.Visible = false;
            DisposeAndClear(pnlMessages);
            _renderedChatKey = null; _renderedChatSig = null;
        }

        // ════════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА И РЕНДЕР СООБЩЕНИЙ
        // ════════════════════════════════════════════════════════════════
        private void LoadMessages(bool markRead = true)
        {
            if (_currentChatPartnerId < 0) return;
            int partner = _currentChatPartnerId;
            int myId = UserSession.EffectiveId;

            // 1) Мгновенно рисуем из кеша (если уже открывали этот чат) — чтобы
            //    переключение между чатами было без задержек, даже под VPN.
            //    Память → диск (постоянный кеш переписки) → пусто.
            if (!_msgMetaCache.TryGetValue(partner, out var cachedDt))
            {
                cachedDt = MessageCache.Load(MessageCache.DirectKey(myId, partner));
                if (cachedDt != null) _msgMetaCache[partner] = cachedDt;
            }
            // Глубину по кешу НЕ поднимаем: дисковый кеш хранит всю долистанную
            // историю и переживает перезапуск — иначе чат прогружался бы целиком при
            // каждом открытии. Глубина НЕ запоминается: вернулся в чат — снова страница.

            // При догрузке вверх кеш (последняя страница) НЕ рисуем — иначе мигало бы
            // и сбивало позицию; ждём свежую большую выборку из БД.
            if (cachedDt != null && !_dmLoadingOlder)
            {
                var (cib, ctb) = _blockCache.TryGetValue(partner, out var bc) ? bc : (false, false);
                // Переход к дате НЕ применяем к отрисовке из кеша: следом придёт свежая
                // (расширенная) выборка и перерисует ленту, сбросив прокрутку вниз —
                // именно из-за этого переход «срабатывал» только со второго клика.
                var savedJump = _pendingJumpDate;
                _pendingJumpDate = null;
                RenderMessages(TakeLastRows(cachedDt, _dmLimit), myId, partner, cib, ctb);
                _pendingJumpDate = savedJump;
            }

            // 2) Свежие данные тянем в ФОНЕ (запросы к БД не на UI-потоке) и
            //    перерисовываем, только если пользователь всё ещё в этом чате.
            System.Threading.Tasks.Task.Run(() =>
            {
                bool iB = false, tB = false;
                DataTable dt = null;
                try
                {
                    iB = IsUserBlocked(myId, partner);
                    tB = IsUserBlocked(partner, myId);
                    dt = LoadMessagesMetaOnly(myId, partner, _dmLimit);
                }
                catch { }
                if (dt == null) return;
                // Медиа страницы забираем ОДНИМ запросом здесь, в фоне: иначе цикл
                // отрисовки лезет в БД за каждой картинкой/голосовым по отдельности.
                try { PrefetchPageMedia(dt, isGroup: false); } catch { }
                _dmHasMore = dt.Rows.Count >= _dmLimit;   // набрали полную страницу → возможно есть ещё
                // Сохраняем в постоянный кеш переписки (текст зашифрован, как в БД).
                try { MessageCache.Save(MessageCache.DirectKey(myId, partner), dt); } catch { }

                // Чат ОТКРЫТ — входящие, пришедшие пока сидим в нём, помечаем
                // прочитанными сразу (раньше read ставился только в момент
                // открытия чата: всё, что писали после, оставалось «непрочитанным»
                // и продолжало давать уведомления даже после визита в чат).
                try
                {
                    if (markRead && _currentChatPartnerId == partner)
                    {
                        bool hasUnreadIncoming = false;
                        if (dt.Columns.Contains("is_read") && dt.Columns.Contains("sender_id"))
                            foreach (DataRow r in dt.Rows)
                                if (Convert.ToInt32(r["sender_id"]) == partner
                                    && r["is_read"] != DBNull.Value && Convert.ToInt32(r["is_read"]) == 0)
                                { hasUnreadIncoming = true; break; }
                        if (hasUnreadIncoming) MarkAsRead(partner);
                    }
                }
                catch { }

                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_currentChatPartnerId != partner) return; // уже переключились
                        _msgMetaCache[partner] = dt;
                        _blockCache[partner] = (iB, tB);
                        RenderMessages(dt, myId, partner, iB, tB);
                    }));
                }
                catch { }
            });
        }

        /// <summary>Отрисовка переписки из готового DataTable (без обращения к БД
        /// за метаданными). Вызывается из кеша мгновенно и из фоновой подгрузки.</summary>
        private void RenderMessages(DataTable dt, int myId, int partner, bool iBlocked, bool theyBlockedMe)
        {
            if (_currentChatPartnerId != partner) return;

            // Пропускаем повторную отрисовку, если тот же чат и данные не изменились.
            string key = "d" + partner, sig = SigOf(dt) + "|b" + (iBlocked ? 1 : 0) + (theyBlockedMe ? 1 : 0);
            if (_renderedChatKey == key && _renderedChatSig == sig) return;
            _renderedChatKey = key; _renderedChatSig = sig;
            try { _pinnedInView = PinsRepository.PinnedIds(0); } catch { _pinnedInView = null; }
            try
            {
                var ids = new List<int>();
                foreach (DataRow rr in dt.Rows) if (rr["id"] != DBNull.Value) ids.Add(Convert.ToInt32(rr["id"]));
                _reactionsInView = ReactionsRepository.ForMessages(ids, ReactionsRepository.Scope.Direct, UserSession.EffectiveId);
            }
            catch { _reactionsInView = null; }

            // Прокрутку сбрасываем в начало ДО очистки и ДО SuspendLayout — пока
            // пузыри ещё на месте. У AutoScroll-панели Top отсчитывается от сдвинутого
            // начала координат: если оставить панель прокрученной, новые пузыри уедут
            // вниз на величину прокрутки и сверху появится большая пустота. На пустой
            // панели с приостановленной раскладкой сброс не срабатывает — начало
            // координат не пересчитывается, поэтому порядок важен.
            try { pnlMessages.AutoScrollPosition = new Point(0, 0); } catch { }
            ChatScroll.SuspendDraw(pnlMessages);   // заморозка отрисовки — без мигания «загрузки заново»
            pnlMessages.SuspendLayout();
            DisposeAndClear(pnlMessages);

            // Если кто-то заблокирован — показываем уведомление и блокируем отправку
            if (iBlocked || theyBlockedMe)
            {
                var notice = new Label
                {
                    Text = iBlocked
                        ? "Вы заблокировали этого пользователя — входящие сообщения скрыты."
                        : "Этот пользователь заблокировал вас — входящие сообщения скрыты.",
                    Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                    ForeColor = Color.FromArgb(185, 187, 190),
                    AutoSize = false,
                    Size = new Size(pnlMessages.ClientSize.Width - 40, 32),
                    Location = new Point(10, 10),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                pnlMessages.Controls.Add(notice);

                // Отключаем отправку в UI — чтобы пользователь видел, что чат в режиме блокировки
                try { btnSend.Enabled = false; txtMessage.Enabled = false; }
                catch { }
            }
            else
            {
                try { btnSend.Enabled = true; txtMessage.Enabled = true; }
                catch { }
            }

            try
            {
                int yOffset = (iBlocked || theyBlockedMe) ? 10 + 32 + 8 : 10;
                string lastDate = "";

                foreach (DataRow row in dt.Rows)
                {
                    int senderId = Convert.ToInt32(row["sender_id"]);
                    bool isMine = senderId == myId;

                    // Если чат в режиме блокировки — скрываем входящие сообщения партнёра
                    if ((iBlocked || theyBlockedMe) && !isMine)
                        continue;

                    string text = Crypto.Dec(row["text"].ToString());
                    string sname = row["sender_name"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(sname)) sname = row["login"].ToString();
                    DateTime dt2 = ToViewerLocal(Convert.ToDateTime(row["created_at"]));
                    string time = dt2.ToString("HH:mm");
                    string date = dt2.ToString("d MMMM yyyy",
                                        new System.Globalization.CultureInfo("ru-RU"));

                    if (date != lastDate)
                    {
                        var sep = BuildDateSeparator(date);
                        sep.Top = yOffset;
                        pnlMessages.Controls.Add(sep);
                        yOffset += sep.Height + 4;
                        lastDate = date;
                    }

                    int msgId = Convert.ToInt32(row["id"]);
                    int replyToId = row["reply_to_id"] == DBNull.Value ? 0 : Convert.ToInt32(row["reply_to_id"]);
                    bool isDeleted = Convert.ToBoolean(row["is_deleted"]);
                    bool isEdited = row["edited_at"] != DBNull.Value;
                    string fileName = row["file_name"] == DBNull.Value ? null : row["file_name"].ToString();

                    bool hasImg = row["has_img"] != DBNull.Value && Convert.ToBoolean(row["has_img"]);
                    bool hasAudio = row["has_audio"] != DBNull.Value && Convert.ToBoolean(row["has_audio"]);
                    bool hasVideo = row["has_video"] != DBNull.Value && Convert.ToBoolean(row["has_video"]);
                    bool hasFile = row["has_file"] != DBNull.Value && Convert.ToBoolean(row["has_file"]);

                    long fileSize = row.Table.Columns.Contains("file_size") && row["file_size"] != DBNull.Value
                        ? Convert.ToInt64(row["file_size"]) : -1;

                    var (img, audio, video, fileData) = LoadMediaForMessage(
                        msgId, fileName, hasImg, hasAudio, hasVideo, hasFile, isGroup: false, fileSize);

                    // Статус прочтения для МОИХ сообщений: 0 — отправляется (1 серая),
                    // 1 — доставлено на сервер (2 серые), 2 — прочитано (2 синие).
                    int readState = -1;
                    if (isMine)
                    {
                        bool isRead = row.Table.Columns.Contains("is_read")
                            && row["is_read"] != DBNull.Value && Convert.ToInt32(row["is_read"]) != 0;
                        // Прочитано → ✓✓ синие; иначе доставлено → ✓✓ серые.
                        // (Состояние «отправляется по возрасту» убрано: оно зависело от
                        // age_sec, которого нет в подписи перерисовки → галочка застывала.)
                        readState = isRead ? 2 : 1;
                    }

                    var bubble = BuildBubble(sname, time, text, img, audio, isMine, video,
                        fileData, fileName, msgId, isGroup: false, replyToId, isDeleted, isEdited, fileSize, readState);
                    bubble.Top = yOffset;
                    PositionBubble(bubble, isMine);
                    bubble.Tag = isMine;
                    // Полная дата отправки — для выпадающего списка результатов поиска.
                    bubble.AccessibleDefaultActionDescription = dt2.ToString("dd.MM.yyyy HH:mm");

                    pnlMessages.Controls.Add(bubble);
                    AddSelectMark(bubble, msgId);
                    yOffset += bubble.Height + 8;
                }

                _lastMsgCount = dt.Rows.Count;
                pnlMessages.ResumeLayout();
                NormalizeTopOffset(pnlMessages);   // подстраховка от «пустоты» сверху

                if (_dmRestoreFromBottom >= 0)
                {
                    // Догрузка старых сообщений сверху: держим позицию — видимые
                    // сообщения остаются на месте (не скидываем вниз). Восстанавливаем
                    // прежнее расстояние от низа контента (DisplayRectangle = полная
                    // высота контента у AutoScroll-панели).
                    pnlMessages.PerformLayout();
                    int viewport = pnlMessages.ClientSize.Height;
                    int newTop = pnlMessages.DisplayRectangle.Height - viewport - _dmRestoreFromBottom;
                    try { pnlMessages.AutoScrollPosition = new Point(0, Math.Max(0, newTop)); } catch { }
                    _dmRestoreFromBottom = -1;
                }
                else if (_pendingJumpDate == null)
                {
                    // Прокручиваем в конец ПОСЛЕ пересчёта layout: иначе диапазон
                    // прокрутки остаётся от предыдущего (длинного) чата и MaxValue
                    // уводит в «пустоту» сверху над короткой перепиской.
                    pnlMessages.PerformLayout();
                    pnlMessages.AutoScrollPosition = new Point(0, int.MaxValue);
                }
                else pnlMessages.PerformLayout();   // ждём переход к дате — вниз не скидываем
                ApplyPendingJump();                 // переход выполняем ПОСЛЕ прокрутки
                _dmLoadingOlder = false;
                UpdateScrollDownButton();
                ChatScroll.ResumeDraw(pnlMessages);   // разморозка ПОСЛЕ восстановления позиции
            }
            catch (Exception ex)
            {
                pnlMessages.ResumeLayout();
                ChatScroll.ResumeDraw(pnlMessages);
                MessageBox.Show("Ошибка загрузки сообщений: " + ex.Message);
            }
        }

        // Подписка на прокрутку панели чата — догрузка старых у верха + кнопка «вниз».
        private bool _dmScrollHooked;
        private int _lastDmTop = int.MaxValue;   // предыдущая позиция прокрутки (для направления)
        private void EnsureDmScrollHook()
        {
            // Кнопка «вниз к новым» убрана из приложения: как отдельное окно поверх
            // прокручиваемой ленты она ломала быстрый путь прокрутки Windows
            // (ScrollWindowEx) и давала рывки/фризы. Прокрутка доступна колесом и полосой.
            // EnsureScrollDownButton();
            // Колесо перехватывается до панели (плавная прокрутка), поэтому логику
            // догрузки/кнопки «вниз» отдаём колбэком — событие MouseWheel уже не придёт.
            try { ChatScroll.AttachChat(pnlMessages, OnChatScrolled); } catch { }
            _lastDmTop = int.MaxValue;
            if (_dmScrollHooked || pnlMessages == null) return;
            _dmScrollHooked = true;
            pnlMessages.Scroll += (s, e) => OnChatScrolled();
        }

        private void OnChatScrolled()
        {
            int top = -pnlMessages.AutoScrollPosition.Y;
            bool movingUp = top < _lastDmTop;      // догружаем ТОЛЬКО при движении вверх
            _lastDmTop = top;
            if (movingUp) MaybeLoadOlder();
            UpdateScrollDownButton();
        }

        /// <summary>У верха списка и есть более старые сообщения — догружаем ещё
        /// страницу (ЛС или группа), сохраняя позицию (чат не скидывается вниз).</summary>
        private void MaybeLoadOlder()
        {
            bool grp = _currentGroupId >= 0;
            bool dm = !grp && _currentChatPartnerId >= 0;
            if (!grp && !dm) return;
            if (_dmLoadingOlder || !_dmHasMore) return;
            if (-pnlMessages.AutoScrollPosition.Y > 60) return;   // ещё не у верха

            _dmLoadingOlder = true;
            int viewport = pnlMessages.ClientSize.Height;
            int curTop = -pnlMessages.AutoScrollPosition.Y;
            _dmRestoreFromBottom = pnlMessages.DisplayRectangle.Height - (curTop + viewport);
            _dmLimit += MsgPageSize;
            // Запоминаем «сколько долистано» — при переоткрытии покажем столько же.
            if (grp) LoadGroupMessages(); else LoadMessages(markRead: false);
        }

        // ── Плавающая кнопка «вниз к новым сообщениям» (ЛС/группы) ───────────
        private void EnsureScrollDownButton()
        {
            if (_btnScrollDown != null || pnlMessages == null) return;
            _btnScrollDown = new Button
            {
                Text = "",
                Size = new Size(55, 55),
                FlatStyle = FlatStyle.Flat,
                BackColor = pnlMessages.BackColor,   // фон = чат: углы сливаются, круг ровный
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Visible = false,
                TabStop = false
            };
            _btnScrollDown.FlatAppearance.BorderSize = 0;
            _btnScrollDown.FlatAppearance.MouseOverBackColor = pnlMessages.BackColor;
            _btnScrollDown.FlatAppearance.MouseDownBackColor = pnlMessages.BackColor;
            // Обрезаем контрол по кругу (эллипс-Region) — без квадратных углов/рамки;
            // сам круг красится чуть внутри (2px), поэтому край остаётся сглаженным.
            try { using var gp = new System.Drawing.Drawing2D.GraphicsPath(); gp.AddEllipse(0, 0, 55, 55);
                  _btnScrollDown.Region = new Region(gp); } catch { }
            _btnScrollDown.Paint += (s, e) => PaintScrollDownCircle(e.Graphics, _btnScrollDown.Width, _btnScrollDown.Height);
            _btnScrollDown.Click += (s, e) => ScrollChatToBottom();
            // Плавное увеличение при наведении.
            _sdAnim = new System.Windows.Forms.Timer { Interval = 12 };
            _sdAnim.Tick += (s, e) => SdAnimTick();
            _btnScrollDown.MouseEnter += (s, e) => { _sdTarget = SdHover; if (!_sdAnim.Enabled) _sdAnim.Start(); };
            _btnScrollDown.MouseLeave += (s, e) => { _sdTarget = SdBase; if (!_sdAnim.Enabled) _sdAnim.Start(); };
            // Кладём поверх панели сообщений (родитель — контейнер панели).
            var host = pnlMessages.Parent ?? (Control)pnlMessages;
            host.Controls.Add(_btnScrollDown);
            _btnScrollDown.BringToFront();
            PositionScrollDownButton();
            host.Resize += (s, e) => PositionScrollDownButton();
        }

        private System.Windows.Forms.Timer _sdAnim;
        private int _sdTarget = 55;
        private const int SdBase = 55, SdHover = 64;
        private void SdAnimTick()
        {
            if (_btnScrollDown == null || _btnScrollDown.IsDisposed) { _sdAnim?.Stop(); return; }
            int cur = _btnScrollDown.Width;
            if (cur == _sdTarget) { _sdAnim.Stop(); return; }
            int step = 3;
            int nw = cur < _sdTarget ? Math.Min(_sdTarget, cur + step) : Math.Max(_sdTarget, cur - step);
            _btnScrollDown.Size = new Size(nw, nw);
            try { using var gp = new System.Drawing.Drawing2D.GraphicsPath(); gp.AddEllipse(0, 0, nw, nw); _btnScrollDown.Region = new Region(gp); } catch { }
            PositionScrollDownButton();   // держим центр
            _btnScrollDown.Invalidate();
        }

        /// <summary>Рисует ровный (сглаженный) синий кружок со стрелкой вниз.
        /// Общий для ЛС/групп и сервера.</summary>
        internal static void PaintScrollDownCircle(Graphics g, int w, int h)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 2, w - 5, h - 5);
            using (var br = new SolidBrush(Color.FromArgb(88, 101, 242)))
                g.FillEllipse(br, r);
            var arrow = Theme.IsLight ? Color.Black : Color.White;   // белая в тёмной, чёрная в светлой
            using var pen = new Pen(arrow, 3f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            int cx = w / 2, cy = h / 2;
            g.DrawLine(pen, cx, cy - 8, cx, cy + 7);                     // стержень
            g.DrawLines(pen, new[] { new Point(cx - 6, cy + 1), new Point(cx, cy + 8), new Point(cx + 6, cy + 1) }); // «галочка» вниз
        }

        private void PositionScrollDownButton()
        {
            if (_btnScrollDown == null) return;
            var host = _btnScrollDown.Parent;
            if (host == null) return;
            // Прижимаем к правому нижнему углу поля сообщений (как на сервере) —
            // раньше центр считался как Right-97 и на широком чате уезжал влево.
            int rightMargin = 20, bottomMargin = 24;
            _btnScrollDown.Location = new Point(
                pnlMessages.Right - _btnScrollDown.Width - rightMargin,
                pnlMessages.Bottom - _btnScrollDown.Height - bottomMargin);
            // BringToFront здесь НЕ вызываем: метод дёргается при каждом ресайзе и на
            // каждом кадре анимации наведения — смена z-order каждый раз давала лишние
            // перерисовки и подтормаживание. Поверх кнопку поднимаем только при показе.
        }

        /// <summary>Показываем кнопку, когда прокрутка не у низа (ушли к старым).</summary>
        private void UpdateScrollDownButton()
        {
            // Кнопка «вниз к новым» убрана из приложения (перекрывающее окно поверх ленты
            // ломало быстрый путь прокрутки Windows и давало рывки/фризы). Метод оставлен
            // пустым — он вызывается из многих мест.
        }

        private void ScrollChatToBottom()
        {
            try { pnlMessages.AutoScrollPosition = new Point(0, int.MaxValue); } catch { }
            UpdateScrollDownButton();
        }

        /// <summary>Последние <paramref name="n"/> строк таблицы (самые новые сообщения).
        /// Нужен, чтобы из дискового кеша, где лежит вся долистанная история, рисовать
        /// только текущую страницу — иначе чат прогружается целиком при открытии.</summary>
        internal static DataTable TakeLastRows(DataTable dt, int n)
        {
            if (dt == null || n <= 0 || dt.Rows.Count <= n) return dt;
            var res = dt.Clone();
            for (int i = dt.Rows.Count - n; i < dt.Rows.Count; i++)
                res.ImportRow(dt.Rows[i]);
            return res;
        }

        /// <summary>Убирает «пустоту» сверху ленты: если из-за сдвинутого начала координат
        /// AutoScroll-панели все пузыри уехали вниз, поднимает их обратно так, чтобы
        /// первый начинался со штатного отступа. Чтение и запись Top идут в одной и той
        /// же системе координат, поэтому сдвиг корректен при любом состоянии прокрутки.</summary>
        private static void NormalizeTopOffset(Panel p)
        {
            if (p == null || p.Controls.Count == 0) return;
            int min = int.MaxValue;
            foreach (Control c in p.Controls) if (c.Top < min) min = c.Top;
            int delta = min - 10;                 // 10 — штатный верхний отступ
            if (delta <= 0 || min == int.MaxValue) return;
            foreach (Control c in p.Controls) c.Top -= delta;
        }

        // ── Переход к сообщениям за дату (кнопка 📅 в поиске) ───────────────
        private DateTime? _pendingJumpDate;   // дата, к которой прокрутиться после отрисовки

        /// <summary>Открывает чат на первом сообщении за выбранную дату. Если нужные
        /// сообщения ещё не подгружены (лента постраничная), сначала расширяем страницу
        /// ровно настолько, чтобы эта дата попала в выборку, и лишь потом прокручиваем.</summary>
        private void JumpToDate(DateTime day)
        {
            bool grp = _currentGroupId >= 0;
            bool dm = !grp && _currentChatPartnerId >= 0;
            if (!grp && !dm) return;

            day = day.Date;
            int need = 0;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = grp
                    ? new MySqlCommand("SELECT COUNT(*) FROM group_messages " +
                                       "WHERE group_id=@g AND created_at >= @d", conn)
                    : new MySqlCommand("SELECT COUNT(*) FROM messages WHERE ((sender_id=@me AND receiver_id=@them) " +
                                       "OR (sender_id=@them AND receiver_id=@me)) AND created_at >= @d", conn);
                if (grp) cmd.Parameters.AddWithValue("@g", _currentGroupId);
                else
                {
                    cmd.Parameters.AddWithValue("@me", UserSession.EffectiveId);
                    cmd.Parameters.AddWithValue("@them", _currentChatPartnerId);
                }
                cmd.Parameters.AddWithValue("@d", day);
                need = Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { }

            if (need <= 0)
            {
                MessageBox.Show(this, $"За {day:dd.MM.yyyy} и позже сообщений нет.",
                    "Переход к дате", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _pendingJumpDate = day;
            if (need + 5 > _dmLimit)
            {
                _dmLimit = need + 5;                 // подтягиваем ленту до нужной даты
                _renderedChatKey = null; _renderedChatSig = null;   // форсим перерисовку
                if (grp) LoadGroupMessages(); else LoadMessages(markRead: false);
            }
            else ApplyPendingJump();                 // всё уже на экране
        }

        /// <summary>Прокручивает к первому сообщению с датой >= выбранной и подсвечивает его.
        /// Дату берём из пузыря — она уже хранится там для списка результатов поиска.</summary>
        private void ApplyPendingJump()
        {
            if (_pendingJumpDate == null) return;
            var day = _pendingJumpDate.Value;

            Panel target = null;
            foreach (Control c in pnlMessages.Controls)
            {
                if (c is not Panel p) continue;
                string s = p.AccessibleDefaultActionDescription;
                if (string.IsNullOrEmpty(s)) continue;
                if (!DateTime.TryParseExact(s, "dd.MM.yyyy HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt)) continue;
                if (dt.Date >= day && (target == null || p.Top < target.Top)) target = p;
            }
            // Цели ещё нет — это отрисовка из кеша (старая страница). Ожидание НЕ
            // сбрасываем: дождёмся свежей выборки, где нужная дата уже есть. Раньше
            // флаг гасился здесь, и переход «съедался» первым же кликом.
            if (target == null) return;
            _pendingJumpDate = null;

            try
            {
                pnlMessages.ScrollControlIntoView(target);
                var orig = target.BackColor;
                target.BackColor = Color.FromArgb(60, 90, 130);
                var t = new System.Windows.Forms.Timer { Interval = 900 };
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); if (!target.IsDisposed) target.BackColor = orig; };
                t.Start();
            }
            catch { }
        }

        private Panel BuildDateSeparator(string dateText)
            => BuildDateSeparatorW(dateText, pnlMessages.ClientSize.Width - 20);

        /// <summary>Разделитель по дате заданной ширины — общий для ЛС и серверов.</summary>
        internal static Panel BuildDateSeparatorW(string dateText, int w)
        {
            var p = new Panel { Width = Math.Max(120, w), Height = 28, BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var penLine = new Pen(Color.FromArgb(60, 255, 255, 255));
                using var fnt = new Font("Segoe UI", 8f);
                var sz = g.MeasureString(dateText, fnt);
                int cx = (p.Width - (int)sz.Width - 12) / 2;
                int cy = p.Height / 2;
                g.DrawLine(penLine, 0, cy, cx - 6, cy);
                g.DrawLine(penLine, cx + (int)sz.Width + 18, cy, p.Width, cy);
                g.DrawString(dateText, fnt, new SolidBrush(Color.FromArgb(114, 118, 125)),
                    cx + 6, cy - sz.Height / 2);
            };
            return p;
        }

        // Строит «пузырёк» сообщения
        private Panel BuildBubble(string senderName, string time, string text,
                                   byte[] imgBytes, byte[] audioBytes, bool isMine,
                                   byte[] videoBytes = null,
                                   byte[] fileData = null, string fileName = null,
                                   int msgId = -1, bool isGroup = false,
                                   int replyToId = 0, bool isDeleted = false,
                                   bool isEdited = false, long fileSize = -1,
                                   int readState = -1)
        {
            const int MAX_W = 480;
            const int PAD = 12;

            var bubble = new Panel
            {
                BackColor = isMine ? Color.FromArgb(88, 101, 242) : Color.FromArgb(48, 51, 58),
                MaximumSize = new Size(MAX_W, 0),
                MinimumSize = new Size(80, 36),
                AutoSize = false,
                Padding = new Padding(PAD),
                AccessibleDescription = (senderName + " " + (text ?? "")).Trim(),  // для поиска по чату (2.0)
                Cursor = Cursors.Default
            };
            ChatScroll.EnableDoubleBufferDeep(bubble);   // пузырь И все дочки (аватар/текст/время) — не «рвутся» при прокрутке

            bubble.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new SolidBrush(bubble.BackColor);
                e.Graphics.FillRoundedRectangle(br, 0, 0, bubble.Width - 1, bubble.Height - 1, 10);
            };
            bubble.Region = System.Drawing.Region.FromHrgn(
                NativeMethods.CreateRoundRectRgn(0, 0, MAX_W, 999, 10, 10));

            int innerY = PAD;
            int innerW = MAX_W - PAD * 2 - 4;

            // Имя отправителя (для чужих сообщений)
            if (!isMine)
            {
                var lblHeader = new Label
                {
                    Text = senderName,
                    Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                    ForeColor = GetAvatarColor(0),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblHeader);
                innerY += lblHeader.PreferredHeight + 2;
            }

            // Цитата ответа (если сообщение является ответом)
            if (!isDeleted && replyToId > 0 && msgId > 0)
            {
                int quoteH = BuildReplyQuote(bubble, replyToId, isGroup, isMine, innerY, innerW, PAD);
                if (quoteH > 0) innerY += quoteH + 4;
            }

            // Удалённое сообщение — показываем плейсхолдер и завершаем рендер
            if (isDeleted)
            {
                var lblDeleted = new Label
                {
                    Text = "🚫 Сообщение удалено",
                    Font = new Font("Segoe UI Italic", 9.5f, FontStyle.Italic),
                    ForeColor = Color.FromArgb(150, isMine ? 255 : 220, isMine ? 255 : 221),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblDeleted);
                innerY += lblDeleted.PreferredHeight + 4;

                var lblTimeDel = new Label
                {
                    Text = time,
                    Font = new Font("Segoe UI", 7.5f),
                    ForeColor = Color.FromArgb(isMine ? 180 : 114, isMine ? 186 : 118, isMine ? 255 : 125),
                    AutoSize = true,
                    Location = new Point(PAD, innerY)
                };
                bubble.Controls.Add(lblTimeDel);
                innerY += lblTimeDel.PreferredHeight + PAD;

                bubble.Size = new Size(Math.Max(160, CalcBubbleWidth(bubble, PAD)), innerY);
                bubble.Region = System.Drawing.Region.FromHrgn(
                    NativeMethods.CreateRoundRectRgn(0, 0, bubble.Width, bubble.Height, 10, 10));
                return bubble;
            }

            // Видео-кружочек (приоритет над картинкой/аудио — самодостаточный элемент)
            if (videoBytes is { Length: > 0 })
            {
                try
                {
                    const int circleD = 220;
                    var player = new VideoCirclePlayer(videoBytes, circleD)
                    {
                        Location = new Point(PAD, innerY)
                    };
                    bubble.Controls.Add(player);
                    innerY += circleD + 6;
                }
                catch
                {
                    bubble.Controls.Add(ErrLabel("⚠ Не удалось загрузить видео-кружочек", PAD, innerY));
                    innerY += 20 + 6;
                }
            }

            // GIF или изображение
            if (imgBytes is { Length: > 0 })
            {
                try
                {
                    // Копируем байты чтобы ms жил независимо
                    var ms = new MemoryStream(imgBytes.ToArray());
                    Image img;
                    try { img = Image.FromStream(ms); }
                    catch { ms.Dispose(); throw; }

                    // Рамка вывода больше — фото/GIF читабельнее. Крупные вписываем
                    // в рамку, мелкие увеличиваем до читаемого размера (но не более 2x,
                    // чтобы не «замыливать»).
                    int maxW = 420, maxH = 360;
                    double fit = Math.Min((double)maxW / img.Width, (double)maxH / img.Height);
                    double ratio = Math.Min(fit, 2.0);
                    int dw = Math.Max(1, (int)(img.Width * ratio));
                    int dh = Math.Max(1, (int)(img.Height * ratio));

                    var pb = new PictureBox
                    {
                        SizeMode = PictureBoxSizeMode.StretchImage,
                        Size = new Size(dw, dh),
                        Location = new Point(PAD, innerY),
                        Cursor = Cursors.Hand
                    };

                    // GIF: анимация через Timer.
                    // КРИТИЧНО: PictureBox автоматически анимирует МНОГОКАДРОВЫЙ
                    // Bitmap через ImageAnimator (а он падает в GDI+ "A generic error
                    // occurred"). Поэтому НИКОГДА не присваиваем pb.Image сам GIF или
                    // его Clone() — заранее рендерим каждый кадр в ОТДЕЛЬНЫЙ одно-
                    // кадровый Bitmap, а таймер просто переключает их.
                    if (IsGif(imgBytes) && img is Bitmap gifBmp)
                    {
                        var dimension = new System.Drawing.Imaging.FrameDimension(
                            gifBmp.FrameDimensionsList[0]);
                        int frameCount = Math.Max(1, gifBmp.GetFrameCount(dimension));

                        // Задержки кадров из метаданных GIF (PropertyTagFrameDelay = 0x5100)
                        int[] delays;
                        try
                        {
                            var prop = gifBmp.GetPropertyItem(0x5100);
                            delays = new int[frameCount];
                            for (int i = 0; i < frameCount; i++)
                                delays[i] = Math.Max(50,
                                    BitConverter.ToInt32(prop.Value, i * 4) * 10); // в мс
                        }
                        catch { delays = new int[frameCount]; Array.Fill(delays, 100); }

                        // Заранее извлекаем все кадры как самостоятельные одно-кадровые битмапы.
                        var frames = new Bitmap[frameCount];
                        try
                        {
                            for (int i = 0; i < frameCount; i++)
                            {
                                gifBmp.SelectActiveFrame(dimension, i);
                                var f = new Bitmap(gifBmp.Width, gifBmp.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                using (var g = Graphics.FromImage(f))
                                    g.DrawImage(gifBmp, 0, 0, gifBmp.Width, gifBmp.Height);
                                frames[i] = f;
                            }
                        }
                        catch { /* что извлекли — то и покажем */ }

                        // Исходный многокадровый GIF больше не нужен.
                        img.Dispose();
                        ms.Dispose();

                        int frameIdx = 0;
                        pb.Image = frames[0];

                        if (frameCount > 1)
                        {
                            var gifTimer = new System.Windows.Forms.Timer { Interval = delays[0] };
                            bool disposed = false;
                            gifTimer.Tick += (s, e) =>
                            {
                                if (disposed || pb.IsDisposed) { gifTimer.Stop(); return; }
                                frameIdx = (frameIdx + 1) % frameCount;
                                var nf = frames[frameIdx];
                                if (nf == null) return;
                                pb.Image = nf; // одно-кадровый битмап — ImageAnimator не вызывается
                                gifTimer.Interval = delays[frameIdx];
                            };
                            gifTimer.Start();

                            pb.Disposed += (s, e) =>
                            {
                                disposed = true;
                                gifTimer.Stop();
                                gifTimer.Dispose();
                                foreach (var f in frames) { try { f?.Dispose(); } catch { } }
                            };
                        }
                        else
                        {
                            pb.Disposed += (s, e) =>
                            {
                                foreach (var f in frames) { try { f?.Dispose(); } catch { } }
                            };
                        }
                    }
                    else
                    {
                        pb.Image = img;
                        pb.Disposed += (s, e) => { img.Dispose(); ms.Dispose(); };
                    }

                    var cap = imgBytes;
                    pb.Click += (s, e) => ShowImageFullscreen(cap);
                    bubble.Controls.Add(pb);
                    innerY += dh + 6;
                }
                catch
                {
                    bubble.Controls.Add(ErrLabel("⚠ Не удалось загрузить изображение", PAD, innerY));
                    innerY += 20 + 6;
                }
            }

            // Голосовое сообщение
            if (audioBytes is { Length: > 0 })
            {
                var btnPlay = new Button
                {
                    Text = "▶  Голосовое",
                    FlatStyle = FlatStyle.Flat,
                    BackColor = isMine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(47, 49, 54),
                    ForeColor = Color.FromArgb(220, 221, 222),
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Size = new Size(170, 36),
                    Location = new Point(PAD, innerY),
                    Cursor = Cursors.Hand
                };
                btnPlay.FlatAppearance.BorderSize = 0;

                var capturedAudio = audioBytes;
                btnPlay.Click += (s, e) => PlayAudio(capturedAudio, btnPlay);

                bubble.Controls.Add(btnPlay);
                innerY += btnPlay.Height + 6;
            }

            // Видео-файл со встроенным проигрывателем (как в Telegram): если байты
            // уже загружены — показываем видео прямо в пузыре; иначе обычная карточка.
            bool inlineVideoShown = false;
            if (!string.IsNullOrWhiteSpace(fileName)
                && MediaPlayerForm.IsVideo(Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant())
                && fileData is { Length: > 0 })
            {
                try
                {
                    int boxW = Math.Min(innerW, 280);
                    int boxH = (int)(boxW * 1.2); // вертикальный бокс с леттербоксом
                    var vp = new InlineVideoPlayer(fileData, fileName, boxW, boxH)
                    {
                        Location = new Point(PAD, innerY)
                    };
                    bubble.Controls.Add(vp);
                    innerY += boxH + 6;
                    inlineVideoShown = true;
                }
                catch { inlineVideoShown = false; }
            }

            // Документ / архив (теперь проверяем только fileName, так как fileData загружается по требованию)
            if (!inlineVideoShown && !string.IsNullOrWhiteSpace(fileName))
            {
                var pnlFile = BuildFileCard(fileData, fileName, isMine, innerW, msgId, isGroup, fileSize);
                pnlFile.Location = new Point(PAD, innerY);
                bubble.Controls.Add(pnlFile);
                innerY += pnlFile.Height + 6;
            }

            // Текст (выделяемый — можно выделить часть и скопировать через Ctrl+C
            // или правый клик, а не только всё сообщение целиком).
            if (!string.IsNullOrEmpty(text))
            {
                var txtMsg = MakeSelectableText(text, bubble.BackColor,
                    Color.FromArgb(235, 236, 240), new Font("Segoe UI", 10.5f), innerW);
                txtMsg.Location = new Point(PAD, innerY);
                bubble.Controls.Add(txtMsg);
                innerY += txtMsg.Height + 4;
            }

            // Время (+ "изменено")
            var lblTime = new Label
            {
                Text = isEdited ? $"{time} · изменено" : time,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(isMine ? 180 : 114, isMine ? 186 : 118, isMine ? 255 : 125),
                AutoSize = true,
                Location = new Point(PAD, innerY)
            };
            bubble.Controls.Add(lblTime);

            // Галочки прочтения (только для моих личных сообщений):
            // 0 — ✓ серая (отправляется), 1 — ✓✓ серые (доставлено), 2 — ✓✓ синие (прочитано).
            if (readState >= 0)
            {
                var lblCheck = new Label
                {
                    Text = readState == 0 ? "✓" : "✓✓",
                    Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                    ForeColor = readState == 2
                        ? Color.FromArgb(88, 170, 255)            // прочитано — синие
                        : Color.FromArgb(150, 160, 180),          // доставлено/отправка — серые
                    AutoSize = true,
                    Location = new Point(lblTime.Right + 4, innerY)
                };
                bubble.Controls.Add(lblCheck);
            }

            innerY += lblTime.PreferredHeight + 4;

            // ── Закреплено (2.0): маркер под сообщением ──────────────────
            bool isPinned = false;
            if (msgId > 0)
            {
                try { isPinned = _pinnedInView != null && _pinnedInView.Contains(msgId); } catch { }
                if (isPinned)
                {
                    var pin = new Label
                    {
                        Text = "📌 закреплено",
                        Font = new Font("Segoe UI Emoji", 7.5f),
                        AutoSize = true,
                        ForeColor = Color.FromArgb(isMine ? 210 : 190, isMine ? 214 : 192, isMine ? 255 : 200),
                        BackColor = Color.Transparent,
                        Location = new Point(PAD, innerY)
                    };
                    bubble.Controls.Add(pin);
                    innerY += pin.PreferredHeight + 4;
                }
            }

            // ── Реакции-эмодзи (2.0): чипы под сообщением; клик — снять/поставить ──
            if (msgId > 0)
            {
                var scope = isGroup ? ReactionsRepository.Scope.Group : ReactionsRepository.Scope.Direct;
                List<ReactionsRepository.Reaction> reacts = null;
                // Берём из пакетно загруженного кеша (один запрос на всю отрисовку),
                // а не роундтрип на каждое сообщение.
                if (_reactionsInView != null) _reactionsInView.TryGetValue(msgId, out reacts);
                if (reacts != null && reacts.Count > 0)
                {
                    int rx = PAD, rh = 0;
                    var cntFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
                    foreach (var re in reacts)
                    {
                        // ЦВЕТНОЙ чип (2.1): эмодзи — картинка из DirectWrite-растеризатора
                        // (GDI рисовал их монохромными), счётчик — обычный текст рядом.
                        // Размер эмодзи 20px (2.1.7) — чтобы не вглядываться.
                        const int emSz = 20;
                        var img = EmojiRender.Get(re.Emoji, emSz);
                        string cntText = re.Count.ToString();
                        int txtW = TextRenderer.MeasureText(cntText, cntFont).Width;
                        int imgW = img?.Width ?? emSz;
                        var chip = new Panel
                        {
                            Size = new Size(8 + imgW + 4 + txtW + 7, 28),
                            BackColor = re.Mine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(48, 51, 58),
                            Location = new Point(rx, innerY),
                            Cursor = Cursors.Hand
                        };
                        var capCnt = cntText; var capImgW = imgW;
                        string emo = re.Emoji;
                        // Картинку перечитываем при КАЖДОЙ отрисовке (дешёвый
                        // словарный кеш): когда Twemoji докачается в фоне, чип
                        // перерисуется уже цветным.
                        chip.Paint += (s, e) =>
                        {
                            var g = e.Graphics;
                            var im = EmojiRender.Get(emo, emSz);
                            if (im != null)
                                g.DrawImage(im, 8, (chip.Height - im.Height) / 2);
                            else
                                using (var f0 = new Font("Segoe UI Emoji", 11f))
                                    g.DrawString(emo, f0, Brushes.White, 6, 4);
                            TextRenderer.DrawText(g, capCnt, cntFont,
                                new Point(8 + capImgW + 4, (chip.Height - 16) / 2),
                                Color.White);
                        };
                        Action<string> onEmojiLoaded = em =>
                        {
                            if (em != emo) return;
                            try
                            {
                                if (chip.IsDisposed || !chip.IsHandleCreated) return;
                                chip.BeginInvoke(new Action(() => { try { chip.Invalidate(); } catch { } }));
                            }
                            catch { }
                        };
                        EmojiRender.Loaded += onEmojiLoaded;
                        chip.Disposed += (s, e) => { try { EmojiRender.Loaded -= onEmojiLoaded; } catch { } };
                        RoundCorners(chip, 8);
                        chip.Click += (s, e) => ToggleReactionAndReload(msgId, scope, emo);
                        bubble.Controls.Add(chip);
                        chip.BringToFront();
                        rx += chip.Width + 4;
                        rh = Math.Max(rh, chip.Height);
                    }

                    // «＋» — добавить ещё одну реакцию (виден, когда уже есть хотя бы одна).
                    var addChip = new Label
                    {
                        Text = "＋",
                        Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Size = new Size(30, 28),
                        ForeColor = Color.FromArgb(200, 202, 208),
                        BackColor = Color.FromArgb(48, 51, 58),
                        Location = new Point(rx, innerY),
                        Cursor = Cursors.Hand
                    };
                    RoundCorners(addChip, 8);
                    int mid = msgId; var msc = scope;
                    addChip.Click += (s, e) => ShowQuickReactionPicker(addChip, mid, msc);
                    bubble.Controls.Add(addChip);
                    addChip.BringToFront();
                    rh = Math.Max(rh, addChip.Height);

                    innerY += rh + 6;
                }
            }

            innerY += PAD - 4;

            bubble.Size = new Size(
                Math.Max(120, CalcBubbleWidth(bubble, PAD)),
                innerY);

            bubble.Region = System.Drawing.Region.FromHrgn(
                NativeMethods.CreateRoundRectRgn(0, 0, bubble.Width, bubble.Height, 10, 10));

            // Контекстное меню (реакция/ответ/пересылка/копировать/редактировать/удалить)
            if (msgId > 0)
                AttachBubbleContextMenu(bubble, msgId, isGroup, isMine, text, senderName);

            return bubble;
        }

        /// <summary>Текст сообщения, который можно выделять и копировать (read-only
        /// TextBox без рамки, выглядит как подпись). Высота считается по содержимому.</summary>
        internal static TextBox MakeSelectableText(string text, Color back, Color fore, Font font, int maxW)
        {
            var tb = new TextBox
            {
                Text = text,
                ReadOnly = true,
                Multiline = true,
                WordWrap = true,
                BorderStyle = BorderStyle.None,
                BackColor = back,
                ForeColor = fore,
                Font = font,
                TabStop = false,
                Cursor = Cursors.IBeam,
                ScrollBars = ScrollBars.None
            };
            int w = Math.Min(TextRenderer.MeasureText(text, font).Width + 6, maxW);
            int h = TextRenderer.MeasureText(text, font, new Size(w, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 4;
            tb.Size = new Size(w, h);
            return tb;
        }

        private int CalcBubbleWidth(Panel bubble, int pad)
        {
            int max = 80;
            foreach (Control c in bubble.Controls)
                max = Math.Max(max, c.Right + pad);
            return Math.Min(max, 480);
        }

        private void PositionBubble(Panel bubble, bool isMine)
        {
            int areaW = pnlMessages.ClientSize.Width - 20;
            bubble.Left = isMine
                ? Math.Max(8, areaW - bubble.Width - 8)
                : 8;
        }

        private void RefreshBubblePositions()
        {
            foreach (Control c in pnlMessages.Controls)
                if (c is Panel bubble && bubble.Tag is bool isMine)
                    PositionBubble(bubble, isMine);
        }

        // ════════════════════════════════════════════════════════════════
        //  ГОЛОСОВЫЕ СООБЩЕНИЯ
        // ════════════════════════════════════════════════════════════════
        private void btnVoice_MouseDown(object sender, MouseEventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            try
            {
                _audioStream = new MemoryStream();
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 1)
                };

                if (DeviceSettings.MicrophoneIndex >= 0 &&
                    DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                {
                    _waveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;
                }

                _waveWriter = new WaveFileWriter(_audioStream, _waveIn.WaveFormat);

                float gain = DeviceSettings.MicrophoneGain;
                _waveIn.DataAvailable += (s, ev) =>
                {
                    if (Math.Abs(gain - 1f) > 0.01f)
                    {
                        var buf = ApplyGain(ev.Buffer, ev.BytesRecorded, gain);
                        _waveWriter?.Write(buf, 0, ev.BytesRecorded);
                    }
                    else
                    {
                        _waveWriter?.Write(ev.Buffer, 0, ev.BytesRecorded);
                    }
                };

                _waveIn.StartRecording();
                btnVoice.ForeColor = Color.FromArgb(240, 71, 71);
                btnVoice.Text = "🔴";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Нет доступа к микрофону: " + ex.Message);
            }
        }

        private void btnVoice_MouseUp(object sender, MouseEventArgs e)
        {
            if (_waveIn == null) return;

            try
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
                _waveIn = null;

                _waveWriter.Flush();
                byte[] audioBytes = _audioStream.ToArray();

                _waveWriter.Dispose();
                _waveWriter = null;
                _audioStream.Dispose();
                _audioStream = null;

                btnVoice.ForeColor = Color.FromArgb(142, 146, 151);
                btnVoice.Text = "🎤";

                if (audioBytes.Length > 4000)  // ~0.1 сек минимум
                {
                    if (_currentGroupId >= 0) SendGroupMessage("", null, audioBytes);
                    else SendMessage("", null, audioBytes);
                }
            }
            catch { }
        }

        /// <summary>Применяет множитель усиления к 16-битному PCM моно буферу.</summary>
        private static byte[] ApplyGain(byte[] buffer, int bytesRecorded, float gain)
        {
            var result = new byte[bytesRecorded];
            Array.Copy(buffer, result, bytesRecorded);

            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                short sample = (short)((result[i + 1] << 8) | result[i]);
                int amplified = (int)(sample * gain);
                amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
                result[i] = (byte)(amplified & 0xFF);
                result[i + 1] = (byte)((amplified >> 8) & 0xFF);
            }

            return result;
        }

        // Статический проигрыватель голосовых (для переиспользования в ServersForm).
        private static WaveOutEvent _voiceOutStatic;
        internal static void PlayVoiceClip(byte[] audioBytes, Button btn)
        {
            if (_voiceOutStatic != null)
            {
                try { _voiceOutStatic.Stop(); _voiceOutStatic.Dispose(); } catch { }
                _voiceOutStatic = null;
                btn.Text = "▶  Голосовое";
                return;
            }
            try
            {
                var ms = new MemoryStream(audioBytes);
                var reader = new WaveFileReader(ms);
                _voiceOutStatic = new WaveOutEvent();
                _voiceOutStatic.Init(new PISMO.Native.TapWaveProvider(reader));
                _voiceOutStatic.Play();
                btn.Text = "⏹  Остановить";
                _voiceOutStatic.PlaybackStopped += (s, ev) =>
                {
                    try { btn.BeginInvoke(new Action(() => {
                        btn.Text = "▶  Голосовое";
                        try { _voiceOutStatic?.Dispose(); } catch { }
                        _voiceOutStatic = null;
                        try { reader.Dispose(); ms.Dispose(); } catch { }
                    })); } catch { }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка воспроизведения: " + ex.Message);
            }
        }

        private void PlayAudio(byte[] audioBytes, Button btn)
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
                _waveOut = null;
                btn.Text = "▶  Голосовое";
                return;
            }

            try
            {
                var ms = new MemoryStream(audioBytes);
                var reader = new WaveFileReader(ms);
                _waveOut = new WaveOutEvent();
                _waveOut.Init(new PISMO.Native.TapWaveProvider(reader));
                _waveOut.Play();

                btn.Text = "⏹  Остановить";

                _waveOut.PlaybackStopped += (s, ev) =>
                {
                    try
                    {
                        this.Invoke(() =>
                        {
                            btn.Text = "▶  Голосовое";
                            _waveOut?.Dispose();
                            _waveOut = null;
                        });
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка воспроизведения: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТПРАВКА
        // ════════════════════════════════════════════════════════════════
        private void btnSend_Click(object sender, EventArgs e)
        {
            bool isGroup = _currentGroupId >= 0;

            if (_currentChatPartnerId < 0 && !isGroup)
            {
                MessageBox.Show("Выберите собеседника в левой панели.");
                return;
            }

            // Режим редактирования — сохраняем и выходим
            if (TrySaveEdit()) return;

            string text = txtMessage.Text.Trim();

            // Режим пересылки — одиночная или пачка из множественного выделения
            if (_forwardMsgId >= 0 || _forwardBatch.Count > 0)
            {
                if (TrySendForward()) return;
            }

            // Прикреплённый файл/изображение/GIF
            if (_pendingAttach != null)
            {
                if (_pendingAttach.Kind == AttachKind.Image || _pendingAttach.Kind == AttachKind.Gif)
                {
                    if (isGroup) SendGroupMessage(text, _pendingAttach.Data, null, null, null, null);
                    else SendMessage(text, _pendingAttach.Data, null, null, null, null);
                }
                else
                {
                    if (isGroup) SendGroupMessage(text, null, null, null, _pendingAttach.Data, _pendingAttach.FileName);
                    else SendMessage(text, null, null, null, _pendingAttach.Data, _pendingAttach.FileName);
                }

                _pendingAttach = null;
                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
                txtMessage.Clear();
                ApplyReplyToLastMessage(isGroup);
                return;
            }

            // Совместимость: старый _pendingImageBytes (Ctrl+V без _pendingAttach)
            if (_pendingImageBytes != null)
            {
                if (isGroup) SendGroupMessage(text, _pendingImageBytes, null, null, null, null);
                else SendMessage(text, _pendingImageBytes, null, null, null, null);

                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
                txtMessage.Clear();
                ApplyReplyToLastMessage(isGroup);
                return;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            if (isGroup) SendGroupMessage(text, null, null, null, null, null);
            else SendMessage(text, null, null, null, null, null);

            txtMessage.Clear();
            ApplyReplyToLastMessage(isGroup);
        }

        /// <summary>Короткое превью для карточки чата по содержимому сообщения.</summary>
        private static string PreviewOf(string text, byte[] img, byte[] aud, byte[] vid, string fileName)
            => !string.IsNullOrWhiteSpace(text) ? text
             : img != null ? "📷 Фото"
             : aud != null ? "🎤 Голосовое сообщение"
             : vid != null ? "🎥 Видео"
             : fileName != null ? "📎 " + fileName
             : "";

        /// <summary>Обновляет превью последнего сообщения на карточке ЛОКАЛЬНО —
        /// без перезагрузки всего списка чатов (это давало сильный пролаг
        /// при каждой отправке).</summary>
        private void UpdateCardPreview(List<Panel> panels, int id, string preview)
        {
            try
            {
                preview ??= "";
                if (preview.Length > 42) preview = preview[..42] + "…";
                foreach (var p in panels)
                    if (p.Tag is int t && t == id)
                    {
                        foreach (Control c in p.Controls)
                            if (c is Label l && l.Name == "lastPreview") { l.Text = preview; return; }
                        return;
                    }
            }
            catch { }
        }

        private void SendMessage(string text, byte[] imageData,
    byte[] audioData = null, byte[] videoData = null,
    byte[] fileData = null, string fileName = null)
        {
            int myId = UserSession.EffectiveId;
            int themId = _currentChatPartnerId;

            // Блокировки: если я заблокировал собеседника или он заблокировал меня — не шлём
            try
            {
                if (IsUserBlocked(myId, themId))
                {
                    MessageBox.Show("Нельзя отправлять — вы заблокировали этого пользователя.", "PISMO",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (IsUserBlocked(themId, myId))
                {
                    MessageBox.Show("Нельзя отправлять — этот пользователь заблокировал вас.", "PISMO",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Приватность получателя: «писать могут только друзья».
                if (!FriendsRepository.CanMessage(myId, themId, UserSession.IsAdminActing))
                {
                    MessageBox.Show(
                        "Этот пользователь принимает сообщения только от друзей.\n" +
                        "Отправьте заявку в друзья (правый клик по карточке пользователя).",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch { /* если проверка по какой-то причине упала — допускаем отправку */ }

            // Файл крупного размера отправляем чанками с круговым прогрессом.
            if (fileData != null && fileData.LongLength > 0)
            {
                bool ok = SendFileWithProgress(isGroup: false, target: themId, myId: myId, text: text,
                    imageData: imageData, audioData: audioData, videoData: videoData,
                    fileData: fileData, fileName: fileName);
                if (ok)
                    WebSocketSignalingClient.Instance.SendMessage("new_message", 0, themId, "direct");
                else
                    return;
            }
            else
            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql =
                    "INSERT INTO messages " +
                    "(sender_id, receiver_id, text, image_data, audio_data, video_data, file_data, file_name) " +
                    "VALUES (@s, @r, @t, @img, @aud, @vid, @fd, @fn)";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@s", myId);
                cmd.Parameters.AddWithValue("@r", themId);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));

                AddBlob(cmd, "@img", imageData);
                AddBlob(cmd, "@aud", audioData);
                AddBlob(cmd, "@vid", videoData);
                AddBlob(cmd, "@fd", fileData);
                cmd.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);

                cmd.ExecuteNonQuery();
                WebSocketSignalingClient.Instance.SendMessage("new_message", 0, themId, "direct");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка отправки: " + ex.Message);
                return;
            }

            // Раньше здесь была ПОЛНАЯ перезагрузка списка чатов + повторное
            // открытие чата (OpenChat → LoadMessages второй раз) — отсюда сильный
            // пролаг при каждой отправке. Теперь: одна загрузка сообщений и
            // локальное обновление превью на карточке собеседника.
            LoadMessages();
            UpdateCardPreview(_userPanels, themId,
                PreviewOf(text, imageData, audioData, videoData, fileName));
        }

        /// <summary>
        /// Загружает файловое сообщение на сервер чанками с круговым индикатором
        /// прогресса. Сначала вставляет строку с метаданными (file_data=NULL),
        /// затем дописывает file_data порциями (виден заполняющийся кружок).
        /// Возвращает true при успехе. При любой ошибке/больших файлах вызывающий
        /// код может откатиться на обычную единоразовую вставку.
        /// </summary>
        private bool SendFileWithProgress(bool isGroup, int target, int myId, string text,
            byte[] imageData, byte[] audioData, byte[] videoData, byte[] fileData, string fileName)
        {
            long total = fileData.LongLength;
            string table = isGroup ? "group_messages" : "messages";

            var dlg = new Form
            {
                Text = "Отправка файла",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                ClientSize = new Size(300, 188),
                BackColor = Color.FromArgb(40, 42, 46),
                ControlBox = false
            };
            double prog = 0;     // -1 при завершении не используем; крутим спиннер
            double angle = 0;    // угол вращающегося индикатора (без фейкового %)
            bool cancelled = false;
            MySqlCommand activeCmd = null;     // текущая команда (для отмены)
            MySqlConnection activeConn = null; // текущее соединение (жёсткий обрыв записи при отмене)
            var pic = new Panel { Size = new Size(72, 72), Location = new Point(114, 14), BackColor = Color.Transparent };
            pic.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(6, 6, 58, 58);
                using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 6);
                using var arc = new Pen(Color.FromArgb(88, 101, 242), 6);
                e.Graphics.DrawEllipse(track, rect);
                if (prog >= 1.0)
                    e.Graphics.DrawArc(arc, rect, -90, 360); // готово — полный круг
                else
                    e.Graphics.DrawArc(arc, rect, (float)angle, 110); // крутящийся сегмент
            };
            var lbl = new Label
            {
                Text = $"Отправка {fileName}\n({FormatFileSize(total)})",
                ForeColor = Color.FromArgb(220, 221, 222),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(10, 92), Size = new Size(280, 46),
                Font = new Font("Segoe UI", 9f)
            };
            var btnCancel = new Button
            {
                Text = "Отмена",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                Size = new Size(120, 30),
                Location = new Point(90, 146),
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) =>
            {
                cancelled = true;
                btnCancel.Enabled = false;
                btnCancel.Text = "Отмена…";
                // Cancel() не прерывает уже идущую потоковую запись blob — поэтому
                // вдобавок ЖЁСТКО рвём соединение: запись обрывается сразу, а строку
                // удалит обработчик catch (cancelled=true) на свежем соединении.
                try { activeCmd?.Cancel(); } catch { }
                try { var c = activeConn; c?.Close(); } catch { }
            };
            dlg.Controls.Add(pic);
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(btnCancel);

            var animTimer = new System.Windows.Forms.Timer { Interval = 60 };
            animTimer.Tick += (s, e) => { if (prog < 1.0) { angle = (angle + 24) % 360; pic.Invalidate(); } };
            dlg.Shown += (s, e) => animTimer.Start();
            dlg.FormClosed += (s, e) => { try { animTimer.Stop(); animTimer.Dispose(); } catch { } };

            bool success = false;
            string err = null;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Для плохо сжатых форматов включаем сжатие протокола (меньше байт
                    // по сети). Уже сжатые (zip/rar/jpg/png/mp4/mp3…) шлём без сжатия.
                    string fext = string.IsNullOrEmpty(fileName) ? "" : Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
                    bool precompressed = fext is "zip" or "rar" or "7z" or "gz" or "tar" or "jpg" or "jpeg"
                        or "png" or "gif" or "webp" or "mp4" or "webm" or "mov" or "mkv" or "mp3"
                        or "aac" or "m4a" or "ogg" or "opus" or "flac" or "pdf";
                    using var conn = precompressed ? DBHelper.OpenConnection() : DBHelper.OpenCompressedConnection();
                    activeConn = conn;

                    // Поднимаем серверные сетевые таймауты сессии: запись большого blob
                    // на медленном диске может длиться дольше дефолтных 30с (иначе сервер
                    // рвёт соединение → "Fatal error encountered during command execution").
                    try { using var to = new MySqlCommand("SET SESSION net_read_timeout=600, net_write_timeout=600, wait_timeout=600", conn); to.ExecuteNonQuery(); } catch { }

                    // 1) Строка метаданных, file_data пока NULL (получаем id).
                    string insSql = isGroup
                        ? "INSERT INTO group_messages (group_id, sender_id, text, image_data, audio_data, video_data, file_data, file_name) VALUES (@g,@s,@t,@img,@aud,@vid,NULL,@fn)"
                        : "INSERT INTO messages (sender_id, receiver_id, text, image_data, audio_data, video_data, file_data, file_name) VALUES (@s,@r,@t,@img,@aud,@vid,NULL,@fn)";
                    long newId;
                    using (var ins = new MySqlCommand(insSql, conn))
                    {
                        if (isGroup) { ins.Parameters.AddWithValue("@g", target); ins.Parameters.AddWithValue("@s", myId); }
                        else { ins.Parameters.AddWithValue("@s", myId); ins.Parameters.AddWithValue("@r", target); }
                        ins.Parameters.AddWithValue("@t", Crypto.Enc(text ?? ""));
                        AddBlob(ins, "@img", imageData);
                        AddBlob(ins, "@aud", audioData);
                        AddBlob(ins, "@vid", videoData);
                        ins.Parameters.AddWithValue("@fn", (object)fileName ?? DBNull.Value);
                        ins.ExecuteNonQuery();
                        newId = ins.LastInsertedId;
                    }

                    if (cancelled) { DeleteMsgRow(conn, table, newId); return; }

                    // Ранний пуш: строка уже есть (is_read=0), уведомляем получателя
                    // СРАЗУ, не дожидаясь заливки больших данных (для файла в десятки
                    // МБ new_message раньше уходил только в конце — пуш приходил очень
                    // поздно или терялся). Финальный new_message после заливки обновит
                    // карточку файла у получателя.
                    try
                    {
                        if (isGroup) WebSocketSignalingClient.Instance.SendMessage("new_message", 0, target, "group");
                        else WebSocketSignalingClient.Instance.SendMessage("new_message", 0, target, "direct");
                    }
                    catch { }

                    // 2) Пытаемся записать файл ОДНИМ запросом (быстро, O(n)).
                    try
                    {
                        using var upd = new MySqlCommand($"UPDATE {table} SET file_data=@fd WHERE id=@id", conn);
                        upd.Parameters.Add("@fd", MySqlDbType.LongBlob).Value = fileData;
                        upd.Parameters.AddWithValue("@id", newId);
                        upd.CommandTimeout = 600;
                        activeCmd = upd;
                        upd.ExecuteNonQuery();
                        activeCmd = null;
                        try { dlg.BeginInvoke(() => { prog = 1.0; pic.Invalidate(); }); } catch { }
                    }
                    catch
                    {
                        activeCmd = null;
                        if (cancelled) { try { conn.Close(); } catch { } using var cc = DBHelper.OpenConnection(); DeleteMsgRow(cc, table, newId); return; }
                        // Пакет великоват для max_allowed_packet — откат на порционную дозапись.
                        // Соединение могло «упасть» после fatal — берём свежее.
                        try { conn.Close(); } catch { }
                        using var conn2 = DBHelper.OpenConnection();
                        activeConn = conn2;
                        const int CHUNK = 4 * 1024 * 1024; // 4 МБ (безопасно для дефолтного пакета)
                        long off = 0;
                        using (var clr = new MySqlCommand($"UPDATE {table} SET file_data=NULL WHERE id=@id", conn2))
                        { clr.Parameters.AddWithValue("@id", newId); clr.ExecuteNonQuery(); }
                        while (off < total)
                        {
                            if (cancelled) { DeleteMsgRow(conn2, table, newId); return; }
                            int len = (int)Math.Min(CHUNK, total - off);
                            var chunk = new byte[len];
                            Array.Copy(fileData, off, chunk, 0, len);
                            using (var up2 = new MySqlCommand(
                                $"UPDATE {table} SET file_data = CONCAT(IFNULL(file_data, _binary''), @c) WHERE id=@id", conn2))
                            {
                                up2.Parameters.Add("@c", MySqlDbType.LongBlob).Value = chunk;
                                up2.Parameters.AddWithValue("@id", newId);
                                up2.CommandTimeout = 600;
                                activeCmd = up2;
                                up2.ExecuteNonQuery();
                                activeCmd = null;
                            }
                            off += len;
                            double p = (double)off / total;
                            try { dlg.BeginInvoke(() => { prog = p; pic.Invalidate(); }); } catch { }
                        }
                    }

                    success = true;
                }
                catch (Exception ex) { if (!cancelled) err = ex.Message; }
                finally { try { dlg.BeginInvoke(() => { dlg.Close(); }); } catch { } }
            });

            dlg.ShowDialog(this);
            if (cancelled) return false;            // отменено пользователем — тихо
            if (!success && err != null)
                MessageBox.Show("Ошибка отправки файла: " + err, "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return success;
        }

        /// <summary>Удаляет строку сообщения (после отмены отправки файла).</summary>
        private static void DeleteMsgRow(MySqlConnection conn, string table, long id)
        {
            try { using var d = new MySqlCommand($"DELETE FROM {table} WHERE id=@id", conn); d.Parameters.AddWithValue("@id", id); d.ExecuteNonQuery(); } catch { }
        }

        /// <summary>Добавляет BLOB-параметр (LongBlob) или DBNull, если данных нет.</summary>
        private static void AddBlob(MySqlCommand cmd, string name, byte[] data)
        {
            if (data != null)
                cmd.Parameters.Add(name, MySqlDbType.LongBlob).Value = data;
            else
                cmd.Parameters.AddWithValue(name, DBNull.Value);
        }

        // ════════════════════════════════════════════════════════════════
        //  ВЛОЖЕНИЯ (ИЗОБРАЖЕНИЯ)
        // ════════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════════
        //  ВИДЕО-КРУЖОЧКИ
        // ════════════════════════════════════════════════════════════════
        private void btnVideoCircle_Click(object sender, EventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            using var dlg = new VideoCircleRecordForm();
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ResultVideoData != null)
            {
                if (_currentGroupId >= 0)
                    SendGroupMessage("", null, null, dlg.ResultVideoData);
                else
                    SendMessage("", null, null, dlg.ResultVideoData);
            }
        }

        private void btnAttach_Click(object sender, EventArgs e)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            using var dlg = new OpenFileDialog
            {
                Title = "Выбрать файл для отправки",
                Filter =
                    "Изображения и GIF|*.jpg;*.jpeg;*.png;*.bmp;*.gif|" +
                    "Видео и музыка|*.mp4;*.webm;*.m4v;*.mov;*.mp3;*.wav;*.ogg;*.m4a;*.aac;*.flac;*.opus|" +
                    "Документы|*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.ppt;*.pdf;*.txt;*.rtf|" +
                    "Архивы|*.zip;*.rar;*.7z;*.tar;*.gz|" +
                    "Все файлы|*.*",
                FilterIndex = 1
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;
            AttachFileByPath(dlg.FileName);
        }

        /// <summary>Прикрепить файл по пути (диалог/перетаскивание) — читает, проверяет
        /// размер, определяет тип и показывает превью над полем ввода.</summary>
        internal void AttachFileByPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось прочитать файл:\n" + ex.Message);
                return;
            }

            const long MAX = 64L * 1024 * 1024; // 64 МБ
            if (bytes.Length > MAX)
            {
                MessageBox.Show($"Файл слишком большой ({bytes.Length / 1024 / 1024} МБ).\nМаксимум — 64 МБ.");
                return;
            }

            bool isGif = ext == ".gif";
            bool isImg = !isGif && new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext);

            AttachKind kind;
            if (isGif) kind = AttachKind.Gif;
            else if (isImg) kind = AttachKind.Image;
            else kind = AttachKind.File;

            if (isImg && bytes.Length > 2 * 1024 * 1024)
                bytes = CompressImageIfNeeded(bytes);

            _pendingAttach = new PendingAttachment(bytes, Path.GetFileName(path), kind);
            ShowPreview(_pendingAttach);
        }

        /// <summary>Включает перетаскивание файлов из проводника на контрол → прикрепить.</summary>
        internal void EnableFileDrop(Control c)
        {
            if (c == null) return;
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                e.Effect = (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    ? DragDropEffects.Copy : DragDropEffects.None;
            };
            c.DragDrop += (s, e) =>
            {
                try
                {
                    if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                        AttachFileByPath(files[0]);   // прикрепляем первый файл в превью
                }
                catch { }
            };
        }

        /// <summary>Показывает превью прикреплённого файла/изображения/GIF над полем ввода.</summary>
        private void ShowPreview(PendingAttachment att)
        {
            pnlPreview.Controls.Clear();

            Control icon;
            if (att.Kind == AttachKind.Image || att.Kind == AttachKind.Gif)
            {
                try
                {
                    var pb = new PictureBox
                    {
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(54, 54),
                        Location = new Point(8, 7)
                    };
                    using var ms = new MemoryStream(att.Data);
                    pb.Image = Image.FromStream(ms);
                    icon = pb;
                }
                catch { icon = MakeFileIcon(att.FileName); }
            }
            else
            {
                icon = MakeFileIcon(att.FileName);
            }
            pnlPreview.Controls.Add(icon);

            long kb = att.Data.Length / 1024;
            string sz = kb > 1024 ? $"{kb / 1024.0:F1} МБ" : $"{kb} КБ";
            var lbl = new Label
            {
                Text = $"{att.FileName}  ({sz})",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                Location = new Point(70, 20),
                AutoSize = true
            };
            pnlPreview.Controls.Add(lbl);

            var btnCancel = new Button
            {
                Text = "✕",
                Size = new Size(28, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(185, 187, 190),
                Font = new Font("Segoe UI", 10f),
                Cursor = Cursors.Hand
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Location = new Point(pnlPreview.Width - 36, 18);
            btnCancel.Click += (s, ev) =>
            {
                _pendingAttach = null;
                _pendingImageBytes = null;
                pnlPreview.Visible = false;
                pnlPreview.Controls.Clear();
            };
            pnlPreview.Resize += (s, ev) =>
                btnCancel.Location = new Point(pnlPreview.Width - 36, 18);

            pnlPreview.Controls.Add(btnCancel);
            pnlPreview.Visible = true;
        }

        /// <summary>Универсальная иконка-плейсхолдер для файлов, не являющихся изображением.</summary>
        internal static Panel MakeFileIcon(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');
            Color bg = ext switch
            {
                "pdf" => Color.FromArgb(220, 53, 53),
                "doc" or "docx" => Color.FromArgb(41, 86, 163),
                "xls" or "xlsx" => Color.FromArgb(32, 120, 62),
                "ppt" or "pptx" => Color.FromArgb(198, 69, 30),
                "zip" or "rar" or "7z" or "tar" or "gz" => Color.FromArgb(140, 90, 20),
                "txt" or "rtf" => Color.FromArgb(80, 80, 80),
                _ => Color.FromArgb(88, 101, 242),
            };

            var p = new Panel { Size = new Size(54, 54), Location = new Point(8, 7), BackColor = Color.Transparent };
            p.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillRoundedRectangle(new SolidBrush(bg), 0, 0, 53, 53, 8);
                string label = ext.Length > 0 ? ext.ToUpper() : "FILE";
                using var f = new Font("Segoe UI Black", label.Length > 3 ? 7.5f : 9.5f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(label, f, Brushes.White, new RectangleF(0, 0, 54, 54), sf);
            };
            return p;
        }

        /// <summary>Сжимает изображение в JPEG (качество 78), если оно больше 2 МБ.</summary>
        private static byte[] CompressImageIfNeeded(byte[] data)
        {
            const int threshold = 2 * 1024 * 1024;
            if (data == null || data.Length <= threshold) return data;
            try
            {
                using var ms = new MemoryStream(data);
                using var img = Image.FromStream(ms);
                ImageCodecInfo jpegCodec = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders())
                    if (c.FormatID == ImageFormat.Jpeg.Guid) { jpegCodec = c; break; }
                if (jpegCodec == null) return data;

                var enc = new EncoderParameters(1);
                enc.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 78L);
                using var outMs = new MemoryStream();
                img.Save(outMs, jpegCodec, enc);
                var compressed = outMs.ToArray();
                return compressed.Length < data.Length ? compressed : data;
            }
            catch { return data; }
        }

        /// <summary>Проверяет magic bytes GIF-файла.</summary>
        private static bool IsGif(byte[] data)
            => data != null && data.Length >= 3
               && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46; // "GIF"

        private static Label ErrLabel(string msg, int x, int y) => new Label
        {
            Text = msg,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(240, 71, 71),
            AutoSize = true,
            Location = new Point(x, y)
        };

        /// <summary>Человекочитаемый размер файла.</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "";
            double kb = bytes / 1024.0;
            return kb > 1024 ? $"{kb / 1024.0:F1} МБ" : $"{Math.Max(1, (long)kb)} КБ";
        }

        /// <summary>Карточка документа/архива внутри пузырька — клик загружает с сервера
        /// (с круговым индикатором прогресса), сохраняет и открывает файл.
        /// knownSize — размер файла в байтах (показывается ДО загрузки).</summary>
        internal static Panel BuildFileCard(byte[] fileData, string fileName, bool isMine, int maxW, int msgId, bool isGroup, long knownSize = -1)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.');
            bool isMedia = MediaPlayerForm.IsMedia(ext);
            bool isVideoMedia = MediaPlayerForm.IsVideo(ext);
            long displaySize = fileData != null ? fileData.Length : knownSize;
            // Размер показываем сразу (если известен), даже до загрузки самого файла.
            string actionHint = isMedia ? "нажмите для воспроизведения" : "нажмите для загрузки";
            string szStr = fileData != null
                ? FormatFileSize(fileData.Length)
                : (displaySize > 0 ? $"{FormatFileSize(displaySize)} · {actionHint}" : (isMedia ? "▶ Нажмите, чтобы открыть" : "💾 Нажмите для загрузки"));

            // Прогресс загрузки: -1 = не идёт, 0..1 = доля. Рисуется поверх иконки.
            double dlProgress = -1;   // -1 = не идёт; >=0 = идёт (значение не важно)
            double dlAngle = 0;       // угол вращающегося индикатора (без фейкового %)
            bool downloading = false;
            bool dlCancelled = false;
            MySqlCommand activeDlCmd = null; // текущая команда скачивания (для отмены)

            Color iconBg = ext switch
            {
                "pdf" => Color.FromArgb(220, 53, 53),
                "doc" or "docx" => Color.FromArgb(41, 86, 163),
                "xls" or "xlsx" => Color.FromArgb(32, 120, 62),
                "ppt" or "pptx" => Color.FromArgb(198, 69, 30),
                "zip" or "rar" or "7z" or "tar" or "gz" => Color.FromArgb(140, 90, 20),
                "txt" or "rtf" => Color.FromArgb(80, 80, 80),
                _ => Color.FromArgb(88, 101, 242),
            };

            int cardW = Math.Min(maxW, 280);
            var card = new Panel
            {
                Size = new Size(cardW, 56),
                BackColor = isMine ? Color.FromArgb(71, 82, 196) : Color.FromArgb(47, 49, 54),
                Cursor = Cursors.Hand
            };

            var iconPnl = new Panel { Size = new Size(40, 40), Location = new Point(8, 8), BackColor = Color.Transparent };
            iconPnl.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillRoundedRectangle(new SolidBrush(iconBg), 0, 0, 39, 39, 6);
                string lbl = ext.Length > 0 ? ext.ToUpper() : "?";
                using var f = new Font("Segoe UI Black", lbl.Length > 3 ? 6.5f : 8.5f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(lbl, f, Brushes.White, new RectangleF(0, 0, 40, 40), sf);

                // Индикатор загрузки поверх иконки — честный «крутящийся» сегмент (без %).
                if (dlProgress >= 0)
                {
                    using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                    e.Graphics.FillRoundedRectangle(shade, 0, 0, 39, 39, 6);
                    var rect = new Rectangle(8, 8, 23, 23);
                    using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 3);
                    using var arc = new Pen(Color.White, 3);
                    e.Graphics.DrawEllipse(track, rect);
                    e.Graphics.DrawArc(arc, rect, (float)dlAngle, 110);
                }
            };
            card.Controls.Add(iconPnl);

            var lblName = new Label
            {
                Text = fileName,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(56, 8),
                Size = new Size(cardW - 64, 20),
                AutoEllipsis = true
            };
            card.Controls.Add(lblName);

            var lblSz = new Label
            {
                Text = szStr,
                Font = new Font("Segoe UI", 7.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                Location = new Point(56, 28),
                AutoSize = true
            };
            card.Controls.Add(lblSz);

            void SaveAndOpen()
            {
                using var save = new SaveFileDialog { FileName = fileName, Title = "Сохранить файл" };
                if (save.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllBytes(save.FileName, fileData);
                    try { Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true }); }
                    catch { }
                }
            }

            // Видео/музыку открываем во встроенном проигрывателе (перемотка/громкость),
            // остальные файлы — сохраняем и открываем системно.
            void OpenIt()
            {
                if (isMedia && fileData != null)
                {
                    try { new MediaPlayerForm(fileData, fileName, isVideoMedia).Show(); }
                    catch { SaveAndOpen(); }
                }
                else SaveAndOpen();
            }

            void DoOpen(object s, EventArgs ev)
            {
                // Повторный клик во время загрузки — отмена.
                if (downloading) { dlCancelled = true; try { activeDlCmd?.Cancel(); } catch { } lblSz.Text = "Отмена…"; return; }

                if (fileData != null) { OpenIt(); return; }

                // Кеш — мгновенно.
                if (MediaCache.Has(msgId, "file", fileName))
                {
                    fileData = MediaCache.Get(msgId, "file", fileName);
                    if (fileData != null) { lblSz.Text = FormatFileSize(fileData.Length); OpenIt(); return; }
                }

                // Загрузка одним запросом; индикатор плавно крутим к 90% (точные байты
                // при одиночном чтении не отследить), 100% — по завершении.
                downloading = true;
                dlCancelled = false;
                dlProgress = 0;
                lblSz.Text = "Загрузка… (клик — отмена)";
                try { iconPnl.Invalidate(); } catch { }

                var dlAnim = new System.Windows.Forms.Timer { Interval = 60 };
                dlAnim.Tick += (ts, te) =>
                {
                    if (!downloading) { dlAnim.Stop(); dlAnim.Dispose(); return; }
                    dlAngle = (dlAngle + 24) % 360; // честное вращение, без процентов
                    try { iconPnl.Invalidate(); } catch { }
                };
                dlAnim.Start();

                string table = isGroup ? "group_messages" : "messages";
                System.Threading.Tasks.Task.Run(() =>
                {
                    byte[] result = null;
                    string err = null;
                    try
                    {
                        using var conn = DBHelper.OpenConnection();

                        long total = knownSize;
                        if (total <= 0)
                        {
                            using var szCmd = new MySqlCommand($"SELECT OCTET_LENGTH(file_data) FROM {table} WHERE id=@id", conn);
                            szCmd.Parameters.AddWithValue("@id", msgId);
                            var o = szCmd.ExecuteScalar();
                            total = (o != null && o != DBNull.Value) ? Convert.ToInt64(o) : 0;
                        }

                        if (total <= 0) { err = "Файл пуст"; }
                        else
                        {
                            // Читаем файл ОДНИМ запросом — без SUBSTRING по кускам
                            // (он на каждый кусок перечитывал весь blob → квадратично/медленно).
                            using var cmd = new MySqlCommand(
                                $"SELECT file_data FROM {table} WHERE id=@id", conn);
                            cmd.Parameters.AddWithValue("@id", msgId);
                            cmd.CommandTimeout = 600;
                            activeDlCmd = cmd;
                            var o = cmd.ExecuteScalar();
                            activeDlCmd = null;
                            result = o as byte[];
                            if (result == null || result.Length == 0) err = "Файл пуст";
                        }
                    }
                    catch (Exception ex) { activeDlCmd = null; if (!dlCancelled) err = ex.Message; }

                    try
                    {
                        card.BeginInvoke(() =>
                        {
                            downloading = false;
                            dlProgress = -1;
                            try { iconPnl.Invalidate(); } catch { }

                            if (dlCancelled) { lblSz.Text = szStr; }
                            else if (result != null && result.Length > 0)
                            {
                                fileData = result;
                                MediaCache.Put(msgId, "file", fileData, fileName);
                                lblSz.Text = FormatFileSize(fileData.Length);
                                OpenIt();
                            }
                            else
                            {
                                lblSz.Text = "Ошибка: " + (err ?? "нет данных");
                            }
                        });
                    }
                    catch { }
                });
            }

            card.Click += DoOpen;
            foreach (Control c in card.Controls) c.Click += DoOpen;

            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var br = new SolidBrush(card.BackColor);
                e.Graphics.FillRoundedRectangle(br, 0, 0, card.Width - 1, card.Height - 1, 8);
                card.Region = System.Drawing.Region.FromHrgn(
                    NativeMethods.CreateRoundRectRgn(0, 0, card.Width, card.Height, 8, 8));
            };

            return card;
        }

        /// <summary>Очищает панель сообщений С УНИЧТОЖЕНием дочерних контролов.
        /// Critically: Controls.Clear() НЕ вызывает Dispose, из-за чего встроенные
        /// видео-плееры (WebView2) продолжали играть звук в фоне после смены чата.</summary>
        internal static void DisposeAndClear(Control parent)
        {
            if (parent == null) return;
            for (int i = parent.Controls.Count - 1; i >= 0; i--)
            {
                try { parent.Controls[i].Dispose(); } catch { }
            }
            try { parent.Controls.Clear(); } catch { }
        }

        // Единственное окно просмотра — повторные клики по картинкам не плодят
        // окна, а обновляют изображение в уже открытом окне.
        private static Form _imageViewer;
        private static PictureBox _imageViewerBox;

        internal static void ShowImageFullscreen(byte[] imgBytes)
        {
            var ms = new MemoryStream(imgBytes.ToArray());
            var img = Image.FromStream(ms);

            // Уже открыто — просто меняем картинку и выводим на передний план.
            if (_imageViewer != null && !_imageViewer.IsDisposed && _imageViewerBox != null)
            {
                var oldImg = _imageViewerBox.Image;
                var oldMs = _imageViewerBox.Tag as IDisposable;
                _imageViewerBox.Image = img;
                _imageViewerBox.Tag = ms;   // держим поток живым
                try { oldImg?.Dispose(); } catch { }
                try { oldMs?.Dispose(); } catch { }
                if (_imageViewer.WindowState == FormWindowState.Minimized)
                    _imageViewer.WindowState = FormWindowState.Normal;
                _imageViewer.BringToFront();
                _imageViewer.Activate();
                return;
            }

            var v = new Form
            {
                Text = "Просмотр",
                Size = new Size(900, 700),
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.Black
            };
            var pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Image = img,
                Tag = ms
            };
            v.Controls.Add(pb);
            v.FormClosed += (s, e) =>
            {
                try { pb.Image?.Dispose(); } catch { }
                try { (pb.Tag as IDisposable)?.Dispose(); } catch { }
                _imageViewer = null;
                _imageViewerBox = null;
            };

            _imageViewer = v;
            _imageViewerBox = pb;
            v.Show();
        }

        // Ctrl+V — вставить изображение или файл из буфера
        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    _pendingImageBytes = ms.ToArray();
                    _pendingAttach = new PendingAttachment(_pendingImageBytes, "image.png", AttachKind.Image);
                    ShowPreview(_pendingAttach);
                    e.Handled = true;
                }
                return;
            }

            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count > 0)
                {
                    string path = files[0];
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 64L * 1024 * 1024)
                        {
                            MessageBox.Show("Файл > 64 МБ.");
                            return;
                        }

                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        bool isImg = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(ext);
                        bool isGif = ext == ".gif";
                        var kind = isGif ? AttachKind.Gif : isImg ? AttachKind.Image : AttachKind.File;

                        if (isImg && bytes.Length > 2 * 1024 * 1024)
                            bytes = CompressImageIfNeeded(bytes);

                        _pendingAttach = new PendingAttachment(bytes, Path.GetFileName(path), kind);
                        ShowPreview(_pendingAttach);
                        e.Handled = true;
                    }
                    catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message); }
                }
            }
        }

        private void txtMessage_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                btnSend_Click(this, EventArgs.Empty);
                txtMessage.Clear();
                return;
            }
            e.IsInputKey = false;
        }

        // ════════════════════════════════════════════════════════════════
        //  КНОПКИ УПРАВЛЕНИЯ
        // ════════════════════════════════════════════════════════════════
        private void btnRefresh_Click(object sender, EventArgs e)
        {
            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();

            if (_currentGroupId >= 0)
                LoadGroupMessages();
            else if (_currentChatPartnerId >= 0)
                LoadMessages(markRead: false);   // «Обновить» не гасит непрочитанные

            PollTick(null, null); // разовый опрос непрочитанных/новых (как делал таймер)
        }

        private void btnSettings_Click(object sender, EventArgs e)
            => ShowSettingsMenu(btnSettings);

        /// <summary>Общее меню настроек (профиль/пароль/устройства/тема/…). Можно
        /// вызвать из другого окна (напр. футера серверов), передав свой якорь.</summary>
        public void ShowSettingsMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();

            if (UserSession.IsImpersonating)
                menu.Items.Add("← Вернуться к своему аккаунту", null, (s, ev) =>
                {
                    UserSession.StopImpersonating();
                    lblCurrentUser.Text = UserSession.UserName;
                    RemoveExitImpersonateButton();
                    ClearChat();
                    LoadAllUsersForAdmin();
                });

            menu.Items.Add("👤 Редактировать профиль", null, (s, ev) => OpenProfile());

            menu.Items.Add("🔑 Сменить пароль", null, (s, ev) =>
                new ChangePasswordForm().ShowDialog(this));

            menu.Items.Add("🎛 Настройки устройств (камера/микрофон)", null, (s, ev) =>
                new SettingsForm().ShowDialog(this));

            // Тема — прямо в меню, чтобы не искать в настройках устройств.
            menu.Items.Add(Theme.IsLight ? "🌙 Переключить на тёмную тему" : "☀️ Переключить на светлую тему",
                null, (s, ev) => SwitchThemeWithRestart(this));

            // «Кто может мне писать» — с галочкой на текущем режиме.
            var priv = new ToolStripMenuItem("✉ Кто может мне писать");
            var privAll = new ToolStripMenuItem("Все");
            var privFriends = new ToolStripMenuItem("Только друзья");
            try
            {
                int mode = FriendsRepository.GetDmPrivacy(UserSession.EffectiveId);
                privAll.Checked = mode == 0;
                privFriends.Checked = mode == 1;
            }
            catch { }
            privAll.Click += (s, ev) => FriendsRepository.SetDmPrivacy(UserSession.EffectiveId, 0);
            privFriends.Click += (s, ev) => FriendsRepository.SetDmPrivacy(UserSession.EffectiveId, 1);
            priv.DropDownItems.Add(privAll);
            priv.DropDownItems.Add(privFriends);
            menu.Items.Add(priv);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("🗑 Очистить кеш (переписка + медиа)", null, (s, ev) => ClearAllCaches());

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("🚪 Выйти из аккаунта", null, (s, ev) =>
            {
                // При выходе из аккаунта активный звонок должен прерываться.
                try { if (_activeCall != null && !_activeCall.IsDisposed) _activeCall.Close(); } catch { }
                try { var c = DockCallWindow(); if (c != null && !c.IsDisposed) c.Close(); } catch { }
                HideVoiceDock();
                _pollTimer.Stop();
                _trayIcon.Visible = false;
                UserSession.Clear();
                LoginForm.InvalidateSavedToken(); // чтобы при перезапуске не вошло само
                // Выход из аккаунта = перезапуск на экран входа (не полный выход, как
                // делает крестик через OnFormClosed→Environment.Exit).
                RestartApplication();
            });

            var a = anchor ?? btnSettings;
            menu.Show(a, new Point(0, -menu.Items.Count * 28));
        }

        // ════════════════════════════════════════════════════════════════
        //  ВСПОМОГАТЕЛЬНОЕ
        // ════════════════════════════════════════════════════════════════

        /// <summary>Смена темы из меню: сохраняем выбор и предлагаем перезапуск
        /// (тема зафиксирована на старте — на лету не применяется, чтобы не было
        /// «полусветлого» приложения).</summary>
        internal void SwitchThemeWithRestart(IWin32Window owner)
        {
            DeviceSettings.ThemeMode = Theme.IsLight ? "dark" : "light";
            try { DeviceSettings.Save(); } catch { }
            var r = MessageBox.Show(owner,
                "Тема применится после перезапуска приложения.\n\nПерезапустить сейчас?",
                "PISMO — тема", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) RestartApplication();
        }

        /// <summary>Перезапуск приложения: новый процесс + немедленный выход
        /// (минуя «сворачивание в трей» и прочие перехваты закрытия).</summary>
        public static void RestartApplication()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Application.ExecutablePath) { UseShellExecute = true });
            }
            catch { }
            try { if (Current?._trayIcon != null) Current._trayIcon.Visible = false; } catch { }
            Environment.Exit(0);
        }

        private void MarkAsRead(int senderId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE messages SET is_read=1 WHERE sender_id=@s AND receiver_id=@r AND is_read=0",
                    conn);
                cmd.Parameters.AddWithValue("@s", senderId);
                cmd.Parameters.AddWithValue("@r", UserSession.EffectiveId);
                int affected = cmd.ExecuteNonQuery();

                // Сообщаем отправителю по WS, что его сообщения прочитаны — чтобы у
                // него галочки стали «прочитано» сразу, без переоткрытия чата.
                if (affected > 0)
                    try { WebSocketSignalingClient.Instance.SendMessage("read", 0, UserSession.EffectiveId, "direct"); } catch { }

                // Бейджи — строго на UI-потоке (MarkAsRead зовётся и из фоновой
                // подгрузки сообщений, когда чат открыт).
                void ClearBadge()
                {
                    foreach (var p in _userPanels)
                        if (p.Tag is int id && id == senderId)
                        {
                            foreach (Control c in p.Controls.Cast<Control>().ToList())
                                if (c is Label lb && lb.BackColor == Color.FromArgb(240, 71, 71))
                                    p.Controls.Remove(c);
                        }
                }
                if (InvokeRequired) { try { BeginInvoke((Action)ClearBadge); } catch { } }
                else ClearBadge();
            }
            catch { }
        }

        private static string BuildName(object name, object surname, object login)
        {
            string full = $"{name} {surname}".Trim();
            return string.IsNullOrWhiteSpace(full) ? login.ToString() : full;
        }

        private Color GetAvatarColor(int uid)
        {
            Color[] palette =
            {
                Color.FromArgb(88,  101, 242),
                Color.FromArgb(87,  171, 90),
                Color.FromArgb(240, 71,  71),
                Color.FromArgb(250, 166, 26),
                Color.FromArgb(0,   176, 244),
                Color.FromArgb(235, 69,  158),
                Color.FromArgb(98,  200, 218),
                Color.FromArgb(156, 89,  182),
            };
            return palette[Math.Abs(uid) % palette.Length];
        }

        private bool _reallyExit;               // true — реальный выход (из трея/logout), а не сворачивание

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик = свернуть в трей (приложение продолжает ловить уведомления).
            // Реальный выход — только из меню трея (_reallyExit) или системного
            // завершения (Windows shutdown). Из трея можно развернуть или закрыть.
            if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                // ВАЖНО: не трогаем ShowInTaskbar — присвоение пересоздаёт хендл
                // окна, и у скрытой формы это давало рассинхрон (первый крестик
                // после разворота из трея срабатывал криво). Скрытая форма
                // (Visible=false) и так не висит в таскбаре — Hide() достаточно.
                Hide();
                try { if (_trayIcon != null) _trayIcon.Visible = true; } catch { }
                // Подсказку показываем через PushNotify: прямой ShowBalloonTip молча
                // не срабатывает, если у иконки в трее не задан Icon (та же причина,
                // по которой раньше пропали пуши). Плюс откладываем на BeginInvoke —
                // окно к этому моменту уже спрятано и иконка в трее «устоялась».
                try
                {
                    BeginInvoke(new Action(() => PushNotify("PISMO свёрнут в трей",
                        "Приложение работает и получает уведомления. Правый клик по иконке — закрыть.")));
                }
                catch
                {
                    PushNotify("PISMO свёрнут в трей",
                        "Приложение работает и получает уведомления. Правый клик по иконке — закрыть.");
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _pollTimer?.Stop();
            _presenceTimer?.Stop();
            MarkSelfOffline();
            try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            _waveIn?.Dispose();
            _waveOut?.Dispose();
            base.OnFormClosed(e);
            // Приложение живёт на ApplicationContext (Splash→Login→Main), поэтому
            // закрытие главного окна крестиком НЕ завершало процесс — оставался
            // висеть в фоне. Явно завершаем процесс (фоновые потоки/иконка трея).
            Environment.Exit(0);
        }

        /// <summary>Помечает себя «не в сети» при выходе (best-effort), чтобы
        /// собеседники сразу увидели офлайн, не дожидаясь таймаута heartbeat.</summary>
        private void MarkSelfOffline()
        {
            if (!_presenceColumnsOk) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET last_seen = DATE_SUB(NOW(), INTERVAL 1 HOUR) WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", UserSession.EffectiveId);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        // Обработчики событий, на которые ссылается Designer.
        private void btnSend_MouseEnter(object sender, EventArgs e)
        {
            // Визуальный эффект наведения (можно изменить по вкусу)
            try
            {
                btnSend.BackColor = Color.FromArgb(100, 115, 255);
            }
            catch { /* safe-guard если btnSend ещё не инициализирован */ }
        }

        private void btnSend_MouseLeave(object sender, EventArgs e)
        {
            try
            {
                btnSend.BackColor = Color.FromArgb(88, 101, 242);
            }
            catch { }
        }

        private void pnlInputBar_Resize(object sender, EventArgs e)
        {
            // Раскладка как на сервере: поле ввода слева (Fill), а все иконочные
            // кнопки собраны СПРАВА единым кластером перед «Отправить»
            // (справа налево: Отправить, GIF, ⏺ кружок, 🎤 голос, 📎 файл).
            try
            {
                const int margin = 12;
                var btnY = btnSend.Location.Y;
                int x = pnlInputBar.ClientSize.Width - margin;

                // Отправить — у самого правого края.
                x -= btnSend.Width;
                btnSend.Location = new Point(Math.Max(margin, x), btnY);

                // GIF — слева от «Отправить».
                if (btnGif != null)
                {
                    x -= 6 + btnGif.Width;
                    btnGif.Location = new Point(Math.Max(margin, x), btnY);
                }

                // Иконочные инструменты справа налево: ⏺ кружок, 🎤 голос, 📎 файл.
                foreach (var b in new[] { btnVideoCircle, btnVoice, btnAttach })
                {
                    if (b == null) continue;
                    x -= 2 + b.Width;
                    b.Location = new Point(Math.Max(margin, x), btnY);
                }

                // Поле ввода занимает всё пространство слева до кластера кнопок.
                int newWidth = x - margin - margin;
                if (newWidth < 60) newWidth = 60;
                txtMessage.Location = new Point(margin, txtMessage.Location.Y);
                txtMessage.Size = new Size(newWidth, txtMessage.Height);
            }
            catch { }
        }

        private void pnlMessages_Resize(object sender, EventArgs e)
        {
            // Вызываем существующий метод обновления позиций пузырей.
            RefreshBubblePositions();
        }
    }

    // ── P/Invoke для скруглённых углов ───────────────────────────────
    internal static class NativeMethods
    {
        // ── Gdi32: скруглённые углы ───────────────────────────────────────
        [System.Runtime.InteropServices.DllImport("Gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
            int nWidthEllipse, int nHeightEllipse);

        // ── User32: цвет прогресс-бара (для VideoCircleRecordForm) ────────
        public enum PbColor { Green = 1, Red = 2, Yellow = 3 }

        public static void SetProgressBarColor(ProgressBar pb, PbColor color)
        {
            try { SendMessage(pb.Handle, 0x0410, (IntPtr)(int)color, IntPtr.Zero); }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }

    // ── Extension для скруглённых прямоугольников ─────────────────────
    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush br,
            float x, float y, float w, float h, float r)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(br, path);
        }
    }
    
}