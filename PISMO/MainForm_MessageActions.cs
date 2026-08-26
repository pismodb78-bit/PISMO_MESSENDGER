// ============================================================
//  MainForm — MessageActions + CallSupport (partial)
//  Добавьте этот файл в проект рядом с MainForm.cs
//  Это partial class — расширяет MainForm без правки оригинала
// ============================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    public partial class MainForm
    {
        // ════════════════════════════════════════════════════════════════
        //  СОСТОЯНИЕ: ОТВЕТ / ПЕРЕСЫЛКА / РЕДАКТИРОВАНИЕ
        // ════════════════════════════════════════════════════════════════
        private int _replyToId = -1;
        private string _replyToText = "";
        private string _replyToSender = "";

        private int _editMsgId = -1;   // > 0 = режим редактирования
        private bool _isGroupEdit = false; // редактируем в группе?

        private int _forwardMsgId = -1;   // сообщение для пересылки
        private string _forwardText = "";
        private string _forwardSenderName = "";
        private bool _forwardIsGroup = false;

        // ── Панель «Ответ / Редактирование» над полем ввода ──────────────
        private Panel _pnlReplyBar;
        private Label _lblReplyInfo;
        private Button _btnCancelReply;

        // ── Панель «Пересылка» (плавающая подсказка) ─────────────────────
        private Panel _pnlForwardBar;
        private Label _lblForwardInfo;
        private Button _btnCancelForward;

        // ── Кнопки звонка в заголовке чата ────────────────────────────────
        private Button _btnCallAudio;

        // ── Активный звонок ───────────────────────────────────────────────
        private CallForm _activeCall;

        // ── Polling входящих звонков ──────────────────────────────────────
        private int _lastCheckedCallId = 0;

        // ── Множественное выделение сообщений (переслать/удалить пачкой) ───
        private bool _selectMode;
        private bool _selectIsGroup;
        private readonly System.Collections.Generic.HashSet<int> _selectedMsgIds = new();
        private readonly System.Collections.Generic.Dictionary<int, (string sender, string text, int scope)> _msgMeta = new();
        // Для мгновенного переключения выделения БЕЗ перезагрузки чата (иначе скролл
        // прыгал вниз и шла полная перерисовка на каждый клик).
        private readonly System.Collections.Generic.Dictionary<int, Label> _selMark = new();
        private readonly System.Collections.Generic.Dictionary<int, Control> _selBubble = new();
        private readonly System.Collections.Generic.Dictionary<int, Color> _selBase = new();
        private readonly System.Collections.Generic.List<(string sender, string text, int scope, int id)> _forwardBatch = new();
        private int _forwardSrcScope;      // 0=ЛС, 1=группа, 2=сервер — откуда пересылаем
        private int _forwardSrcId;         // id исходного сообщения (для копии медиа)
        private Panel _pnlSelectBar;
        private Label _lblSelectInfo;

        // ════════════════════════════════════════════════════════════════
        //  ИНИЦИАЛИЗАЦИЯ (вызывать из MainForm_Load или конструктора)
        // ════════════════════════════════════════════════════════════════
        public void InitMessageActions()
        {
            BuildReplyBar();
            BuildVoiceBar();     // полоса записанного голосового, см. MainForm_VoiceNote
            BuildForwardBar();
            BuildSelectBar();
            AddCallButtonsToHeader();
            HookCallPolling();
        }

        // ─────────────────────────────────────────────────────────────────
        //  ПАНЕЛЬ ОТВЕТА (над строкой ввода)
        // ─────────────────────────────────────────────────────────────────
        private void BuildReplyBar()
        {
            _pnlReplyBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                BackColor = Color.FromArgb(47, 49, 54),
                Visible = false
            };

            _lblReplyInfo = new Label
            {
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(0, 176, 244),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            _btnCancelReply = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(185, 187, 190),
                Cursor = Cursors.Hand
            };
            _btnCancelReply.FlatAppearance.BorderSize = 0;
            _btnCancelReply.Click += (s, e) => CancelReply();

            _pnlReplyBar.Controls.Add(_lblReplyInfo);
            _pnlReplyBar.Controls.Add(_btnCancelReply);

            // Вставляем над pnlInputBar
            pnlMain.Controls.Add(_pnlReplyBar);
            pnlMain.Controls.SetChildIndex(_pnlReplyBar,
                pnlMain.Controls.IndexOf(pnlInputBar));
        }

        private void BuildForwardBar()
        {
            _pnlForwardBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                BackColor = Color.FromArgb(47, 49, 54),
                Visible = false
            };

            _lblForwardInfo = new Label
            {
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(250, 166, 26),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            _btnCancelForward = new Button
            {
                Text = "✕",
                Dock = DockStyle.Right,
                Width = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(185, 187, 190),
                Cursor = Cursors.Hand
            };
            _btnCancelForward.FlatAppearance.BorderSize = 0;
            _btnCancelForward.Click += (s, e) => CancelForward();

            _pnlForwardBar.Controls.Add(_lblForwardInfo);
            _pnlForwardBar.Controls.Add(_btnCancelForward);

            pnlMain.Controls.Add(_pnlForwardBar);
            pnlMain.Controls.SetChildIndex(_pnlForwardBar,
                pnlMain.Controls.IndexOf(pnlInputBar));
        }

        // ─────────────────────────────────────────────────────────────────
        //  КНОПКИ ЗВОНКА В ЗАГОЛОВКЕ
        // ─────────────────────────────────────────────────────────────────
        private void AddCallButtonsToHeader()
        {
            _btnCallAudio = new Button
            {
                Text = "📞",
                Font = new Font("Segoe UI", 13f),
                Size = new Size(38, 38),
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(87, 171, 90),
                Cursor = Cursors.Hand
            };
            _btnCallAudio.FlatAppearance.BorderSize = 0;
            // Одна кнопка звонка: входим в звонок (голос), а камеру/демонстрацию
            // можно включить уже внутри звонка. Отдельной видео-кнопки больше нет.
            _btnCallAudio.Click += (s, e) => StartCall(withVideo: false);
            new ToolTip().SetToolTip(_btnCallAudio, "Позвонить (камеру можно включить в звонке)");

            pnlChatHeader.Controls.Add(_btnCallAudio);
        }

        // ════════════════════════════════════════════════════════════════
        //  КОНТЕКСТНОЕ МЕНЮ ПУЗЫРЯ (правая кнопка)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Вызывать из BuildBubble для каждого пузыря.
        /// msgId — ID в таблице messages или group_messages.
        /// isGroup — true если групповое.
        /// isMine — наше сообщение?
        /// text — текст сообщения (для edit/forward).
        /// </summary>
        // (Окно «нажмите Win + .» и белое подменю быстрых реакций удалены —
        //  «Реакция» открывает тёмный пикер EmojiPickerForm, где первая вкладка
        //  🕒 «часто используемые» и есть быстрые реакции.)

        /// <summary>Кнопка «＋» под сообщением — добавить ещё одну реакцию, когда
        /// одна уже стоит. Открывает тот же тёмный пикер эмодзи.</summary>
        private void ShowQuickReactionPicker(Control anchor, int msgId, ReactionsRepository.Scope scope)
        {
            try
            {
                var pt = anchor != null ? anchor.PointToScreen(new Point(0, anchor.Height)) : Cursor.Position;
                string emo = EmojiPickerForm.Pick(this, pt);
                if (!string.IsNullOrWhiteSpace(emo)) ToggleReactionAndReload(msgId, scope, emo);
            }
            catch { }
        }

        /// <summary>Ставит/снимает реакцию и перерисовывает открытый чат.</summary>
        private void ToggleReactionAndReload(int msgId, ReactionsRepository.Scope scope, string emoji)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    ReactionsRepository.Toggle(msgId, scope, UserSession.EffectiveId, emoji);
                    // Сообщаем собеседникам, чтобы они увидели реакцию сразу.
                    try
                    {
                        if (scope == ReactionsRepository.Scope.Group && _currentGroupId > 0)
                            WebSocketSignalingClient.Instance.SendMessage("reaction", 0, _currentGroupId, "group");
                        else if (_currentChatPartnerId > 0)
                            WebSocketSignalingClient.Instance.SendMessage("reaction", _currentChatPartnerId, UserSession.EffectiveId, "direct");
                    }
                    catch { }
                }
                catch { }
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        ForceMessageRerender();   // реакции изменились — данные те же, форсим перерисовку
                        if (_currentGroupId > 0) LoadGroupMessages();
                        else if (_currentChatPartnerId > 0) LoadMessages();
                    }));
                }
                catch { }
            });
        }

        // ════════════════════════════════════════════════════════════════
        //  МНОЖЕСТВЕННОЕ ВЫДЕЛЕНИЕ (переслать / удалить несколько сразу)
        // ════════════════════════════════════════════════════════════════
        private void BuildSelectBar()
        {
            _pnlSelectBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = Color.FromArgb(47, 49, 54),
                Visible = false
            };

            _lblSelectInfo = new Label
            {
                Dock = DockStyle.Left,
                Width = 170,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Padding = new Padding(14, 0, 0, 0),
                Text = "Выбрано: 0"
            };

            Button Bar(string t, Color fg)
            {
                var b = new Button
                {
                    Text = t,
                    Dock = DockStyle.Right,
                    Width = 130,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(54, 57, 63),
                    ForeColor = fg,
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                return b;
            }

            var btnCancel = Bar("Отмена", Color.FromArgb(200, 202, 208));
            var btnDelete = Bar("🗑  Удалить", Color.FromArgb(240, 71, 71));
            var btnForward = Bar("↪  Переслать", Color.FromArgb(88, 170, 255));
            btnCancel.Click += (s, e) => ExitSelectMode();
            btnDelete.Click += (s, e) => DeleteSelected();
            btnForward.Click += (s, e) => ForwardSelected();

            _pnlSelectBar.Controls.Add(_lblSelectInfo);
            _pnlSelectBar.Controls.Add(btnCancel);
            _pnlSelectBar.Controls.Add(btnDelete);
            _pnlSelectBar.Controls.Add(btnForward);

            pnlMain.Controls.Add(_pnlSelectBar);
            _pnlSelectBar.BringToFront();
        }

        private void EnterSelectMode(bool isGroup)
        {
            _selectMode = true;
            _selectIsGroup = isGroup;
            _selectedMsgIds.Clear();
            _selMark.Clear(); _selBubble.Clear(); _selBase.Clear();
            if (_pnlSelectBar != null) { _pnlSelectBar.Visible = true; _pnlSelectBar.BringToFront(); }
            UpdateSelectBar();
            RerenderCurrentChat();   // один раз: показать кружки ○ у всех сообщений
        }

        private void ExitSelectMode()
        {
            _selectMode = false;
            _selectedMsgIds.Clear();
            if (_pnlSelectBar != null) _pnlSelectBar.Visible = false;
            RerenderCurrentChat();
        }

        private void ToggleSelect(int msgId)
        {
            if (msgId <= 0) return;
            if (!_selectedMsgIds.Add(msgId)) _selectedMsgIds.Remove(msgId);
            UpdateSelectBar();

            // ТОЧЕЧНОЕ обновление вида выбранного пузыря — БЕЗ перезагрузки чата
            // (раньше LoadMessages на каждый клик прокручивал чат вниз и тормозил).
            bool sel = _selectedMsgIds.Contains(msgId);
            if (_selMark.TryGetValue(msgId, out var mk) && mk != null && !mk.IsDisposed)
            {
                mk.Text = sel ? "✔" : "○";
                mk.ForeColor = sel ? Color.FromArgb(59, 165, 93) : Color.FromArgb(150, 152, 158);
            }
            if (_selBubble.TryGetValue(msgId, out var bb) && bb != null && !bb.IsDisposed
                && _selBase.TryGetValue(msgId, out var baseColor))
            {
                bb.BackColor = sel ? ControlPaint.Light(baseColor, 0.15f) : baseColor;
            }
        }

        private void UpdateSelectBar()
        {
            if (_lblSelectInfo != null) _lblSelectInfo.Text = $"Выбрано: {_selectedMsgIds.Count}";
        }

        private void RerenderCurrentChat()
        {
            int savedScroll = 0;
            try { savedScroll = -pnlMessages.AutoScrollPosition.Y; } catch { }
            try
            {
                ForceMessageRerender();
                if (_currentGroupId > 0) LoadGroupMessages();
                else if (_currentChatPartnerId >= 0) LoadMessages();
            }
            catch { }
            // LoadMessages форсит скролл вниз — возвращаем позицию, чтобы вход в режим
            // выделения / правка не прыгали к последнему сообщению.
            try { pnlMessages.AutoScrollPosition = new Point(0, savedScroll); } catch { }
        }

        private void DeleteSelected()
        {
            if (_selectedMsgIds.Count == 0) { ExitSelectMode(); return; }
            if (MessageBox.Show(
                $"Удалить выбранные сообщения ({_selectedMsgIds.Count})? Это нельзя отменить.",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            string table = _selectIsGroup ? "group_messages" : "messages";
            var ids = new System.Collections.Generic.List<int>(_selectedMsgIds);
            try
            {
                using var conn = DBHelper.OpenConnection();
                foreach (int id in ids)
                {
                    using var cmd = new MySqlCommand(
                        $"UPDATE {table} SET is_deleted=1, text='[сообщение удалено]', " +
                        "image_data=NULL, audio_data=NULL, video_data=NULL WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { MessageBox.Show("Ошибка удаления: " + ex.Message); }

            _selectMode = false;
            _selectedMsgIds.Clear();
            if (_pnlSelectBar != null) _pnlSelectBar.Visible = false;
            NotifyChatEdited(_selectIsGroup);
            RerenderCurrentChat();
        }

        private void ForwardSelected()
        {
            if (_selectedMsgIds.Count == 0) { ExitSelectMode(); return; }

            // Собираем выбранные по возрастанию id (порядок как в чате).
            var ids = new System.Collections.Generic.List<int>(_selectedMsgIds);
            ids.Sort();
            _forwardBatch.Clear();
            foreach (int id in ids)
                if (_msgMeta.TryGetValue(id, out var meta))
                    _forwardBatch.Add((meta.sender, meta.text, meta.scope, id));

            int cnt = _forwardBatch.Count;
            _selectMode = false;
            _selectedMsgIds.Clear();
            if (_pnlSelectBar != null) _pnlSelectBar.Visible = false;

            _lblForwardInfo.Text = $"↪ Пересылка {cnt} сообщений(я) — выберите диалог и нажмите «Отправить»";
            _pnlForwardBar.Visible = true;
            RerenderCurrentChat();

            MessageBox.Show(
                "Выберите диалог, группу или канал сервера и нажмите «Отправить».\n" +
                $"Будут пересланы выбранные сообщения ({cnt}).",
                "PISMO — Пересылка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            txtMessage.Focus();
        }

        /// <summary>Добавляет в меню «Скачать» для того вложения, которое реально есть
        /// в сообщении. Общий код для ЛС, групп и каналов серверов.</summary>
        internal static void AddDownloadItem(ContextMenuStrip menu, int msgId,
            byte[] img, byte[] audio, byte[] video, byte[] file, string fileName,
            Func<byte[]> fileLoader = null)
        {
            string caption = null; byte[] data = null; string name = null;
            Func<byte[]> loader = null;

            // Порядок важен: у видео-кружка и видео байты лежат в одном поле, а
            // подпись к файлу может идти вместе с картинкой.
            if (file != null && file.Length > 0)
            { caption = "⬇  Скачать файл"; data = file; name = string.IsNullOrWhiteSpace(fileName) ? "pismo_file" : fileName; }
            else if (video != null && video.Length > 0)
            {
                // Кружок лежит в своём контейнере PSMOVID1 — переупаковываем его в
                // AVI (MJPG + PCM), иначе скачанный файл не открывает ни один плеер.
                if (VideoCircleExport.IsCircle(video))
                {
                    caption = "⬇  Скачать кружок";
                    data = video;                       // конвертация — в момент сохранения
                    name = MediaSaver.VideoName(msgId, circle: true);
                }
                else
                {
                    caption = "⬇  Скачать видео"; data = video;
                    name = $"pismo_video_{(msgId > 0 ? msgId.ToString() : "file")}.{MediaSaver.VideoExt(video)}";
                }
            }
            else if (img != null && img.Length > 0)
            { caption = "⬇  Скачать изображение"; data = img; name = MediaSaver.ImageName(img, msgId); }
            else if (audio != null && audio.Length > 0)
            { caption = "⬇  Скачать голосовое"; data = audio; name = MediaSaver.AudioName(msgId); }
            else if (fileLoader != null && !string.IsNullOrWhiteSpace(fileName))
            {
                // Крупные вложения в ленту не подгружаются — байтов тут ещё нет.
                // Пункт всё равно показываем, файл читается уже по нажатию.
                caption = "⬇  Скачать файл"; name = fileName; loader = fileLoader;
            }

            if (caption == null) return;

            var item = new ToolStripMenuItem(caption);
            byte[] capData = data; string capName = name; var capLoader = loader;
            item.Click += (s, e) =>
            {
                var owner = menu.SourceControl?.FindForm();
                byte[] bytes = capData;
                if (bytes == null && capLoader != null)
                {
                    var prev = Cursor.Current;
                    try { Cursor.Current = Cursors.WaitCursor; bytes = capLoader(); }
                    catch { }
                    finally { Cursor.Current = prev; }
                }
                if (bytes == null || bytes.Length == 0)
                {
                    MessageBox.Show(owner, "Не удалось получить файл.", "PISMO",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MediaSaver.Save(owner, bytes, capName);
            };
            menu.Items.Add(item);
            menu.Items.Add(new ToolStripSeparator());
        }

        /// <summary>
        /// Правый клик по пузырю. Меню НЕ строится при отрисовке ленты: раньше
        /// BuildBubble звал этот метод на каждое сообщение, и каждый вызов
        /// спрашивал у базы «закреплено ли сообщение» — отдельный запрос на
        /// сообщение. Пока сервер стоял рядом, это было незаметно; после
        /// переезда на VPS круг до сервера ~130 мс, и сорок сообщений давали
        /// восемь секунд замершего окна на каждую отрисовку. Теперь меню
        /// (и его запрос) появляются только когда по сообщению реально
        /// щёлкнули правой кнопкой.
        /// </summary>
        public void AttachBubbleContextMenu(Panel bubble, int msgId, bool isGroup,
    bool isMine, string text, string senderName,
    byte[] imgBytes = null, byte[] audioBytes = null, byte[] videoBytes = null,
    byte[] fileData = null, string fileName = null)
        {
            // Запоминаем текст/отправителя — понадобится для пакетной пересылки.
            if (msgId > 0) _msgMeta[msgId] = (senderName ?? "", text ?? "", isGroup ? 1 : 0);

            ContextMenuStrip menu = null;
            ContextMenuStrip Menu() => menu ??= BuildBubbleMenu(
                bubble, msgId, isGroup, isMine, text, senderName,
                imgBytes, audioBytes, videoBytes, fileData, fileName);

            // Правый клик — меню; левый клик в режиме выделения — отметить/снять.
            void ShowMenu(object? s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                    Menu().Show(Cursor.Position);
                else if (e.Button == MouseButtons.Left && _selectMode && msgId > 0)
                    ToggleSelect(msgId);
            }

            bubble.MouseClick += ShowMenu;
            foreach (Control c in bubble.Controls)
            {
                // У выделяемого TextBox правый клик иначе показал бы родное меню.
                // Вешаем пустую заглушку: она отменяет своё открытие и показывает
                // наше меню — построенное в этот момент, а не заранее.
                if (c is TextBox tb)
                {
                    var holder = new ContextMenuStrip();
                    holder.Opening += (s, ce) => { ce.Cancel = true; Menu().Show(Cursor.Position); };
                    tb.ContextMenuStrip = holder;
                }
                else c.MouseClick += ShowMenu;
            }

            // Подсветка выбранного сообщения в режиме выделения. Запоминаем пузырь и
            // его БАЗОВЫЙ цвет, чтобы ToggleSelect мог точечно тонировать/снимать
            // подсветку без перезагрузки чата.
            if (_selectMode && msgId > 0)
            {
                _selBubble[msgId] = bubble;
                _selBase[msgId] = bubble.BackColor;
                if (_selectedMsgIds.Contains(msgId))
                    bubble.BackColor = ControlPaint.Light(bubble.BackColor, 0.15f);
            }
        }

        /// <summary>Собирает меню пузыря. Зовётся по требованию — из ShowMenu.</summary>
        private ContextMenuStrip BuildBubbleMenu(Panel bubble, int msgId, bool isGroup,
            bool isMine, string text, string senderName,
            byte[] imgBytes, byte[] audioBytes, byte[] videoBytes,
            byte[] fileData, string fileName)
        {
            var menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(24, 25, 28);
            menu.ForeColor = Color.FromArgb(220, 221, 222);
            menu.Font = new Font("Segoe UI", 9.5f);

            // ── Выбрать (множественное выделение) ────────────────────────
            if (msgId > 0)
            {
                var itemSelect = new ToolStripMenuItem("☑  Выбрать");
                itemSelect.Click += (s, e) =>
                {
                    if (!_selectMode) EnterSelectMode(isGroup);
                    ToggleSelect(msgId);
                };
                menu.Items.Add(itemSelect);
                menu.Items.Add(new ToolStripSeparator());
            }

            // ── Скачать вложение ─────────────────────────────────────────
            // Байты уже у клиента (пришли вместе с сообщением) — обращаться к БД
            // не нужно, только диалог сохранения.
            // fileData бывает пустым намеренно: большие файлы читаются по клику.
            Func<byte[]> lazyFile = (msgId > 0 && !string.IsNullOrWhiteSpace(fileName)
                                     && !(fileData is { Length: > 0 }))
                ? () => LoadFileBytes(msgId, fileName, isGroup)
                : null;
            AddDownloadItem(menu, msgId, imgBytes, audioBytes, videoBytes, fileData, fileName, lazyFile);

            // ── Реакция (эмодзи) ─────────────────────────────────────────
            if (msgId > 0)
            {
                // Белое системное подменю с быстрыми реакциями убрано (2.1.2) —
                // «Реакция» сразу открывает наш тёмный пикер: первая вкладка 🕒
                // «часто используемые» и есть быстрые реакции, целой сеткой.
                var scope = isGroup ? ReactionsRepository.Scope.Group : ReactionsRepository.Scope.Direct;
                var itemReact = new ToolStripMenuItem("😀  Реакция");
                itemReact.Click += (s, e) =>
                {
                    string emo = EmojiPickerForm.Pick(this, Cursor.Position);
                    if (!string.IsNullOrWhiteSpace(emo)) ToggleReactionAndReload(msgId, scope, emo);
                };
                menu.Items.Add(itemReact);
                menu.Items.Add(new ToolStripSeparator());
            }

            // ── Закрепить / открепить ────────────────────────────────────
            if (msgId > 0)
            {
                int pinScope = isGroup ? 1 : 0;
                bool pinned = false;
                try { pinned = PinsRepository.IsPinned(msgId, pinScope); } catch { }
                var itemPin = new ToolStripMenuItem(pinned ? "📌  Открепить" : "📌  Закрепить");
                itemPin.Click += (s, e) =>
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { PinsRepository.Toggle(msgId, pinScope, UserSession.EffectiveId); } catch { }
                        if (IsDisposed || !IsHandleCreated) return;
                        try { BeginInvoke(new Action(() => { ForceMessageRerender(); if (_currentGroupId > 0) LoadGroupMessages(); else if (_currentChatPartnerId > 0) LoadMessages(); })); } catch { }
                    });
                };
                menu.Items.Add(itemPin);
            }

            // ── Ответить ─────────────────────────────────────────────────
            var itemReply = new ToolStripMenuItem("↩  Ответить");
            itemReply.Click += (s, e) => BeginReply(msgId, senderName, text);
            menu.Items.Add(itemReply);

            // ── Переслать ────────────────────────────────────────────────
            var itemForward = new ToolStripMenuItem("↪  Переслать");
            itemForward.Click += (s, e) => BeginForward(msgId, text, isGroup, senderName);
            menu.Items.Add(itemForward);

            // ── Копировать текст (выделенное либо всё) ───────────────────
            if (!string.IsNullOrEmpty(text))
            {
                // Находим выделяемый TextBox с текстом сообщения внутри пузыря.
                TextBox FindTextBox()
                {
                    foreach (Control c in bubble.Controls)
                        if (c is TextBox tb) return tb;
                    return null;
                }

                var itemCopy = new ToolStripMenuItem("📋  Копировать");
                itemCopy.Click += (s, e) =>
                {
                    var tb = FindTextBox();
                    string sel = tb != null && tb.SelectionLength > 0 ? tb.SelectedText : text;
                    try { Clipboard.SetText(sel); } catch { }
                };
                menu.Items.Add(itemCopy);
            }

            // ── Редактировать (только своё текстовое) ────────────────────
            if (isMine && !string.IsNullOrEmpty(text))
            {
                menu.Items.Add(new ToolStripSeparator());
                var itemEdit = new ToolStripMenuItem("✏  Редактировать");
                itemEdit.Click += (s, e) => BeginEdit(msgId, isGroup, text);
                menu.Items.Add(itemEdit);
            }

            // ── История изменений (2.0) ──────────────────────────────────
            if (msgId > 0)
            {
                var itemHist = new ToolStripMenuItem("📝  История изменений");
                itemHist.Click += (s, e) => ShowEditHistory(msgId, isGroup ? 1 : 0);
                menu.Items.Add(itemHist);
            }

            // ── Удалить сообщение (своё или admin) ───────────────────────
            bool canDelete = isMine || UserSession.Role == "admin";
            if (canDelete)
            {
                var itemDel = new ToolStripMenuItem("🗑  Удалить сообщение")
                { ForeColor = Color.FromArgb(240, 71, 71) };
                itemDel.Click += (s, e) => DeleteMessage(msgId, isGroup);
                menu.Items.Add(itemDel);
            }

            return menu;
        }

        /// <summary>Кружок выделения СПРАВА от сообщения (как в Telegram) — сосед
        /// пузыря в pnlMessages, у правого края чата. ✔ если выбрано.</summary>
        private void AddSelectMark(Control bubble, int msgId)
        {
            if (!_selectMode || msgId <= 0) return;
            bool sel = _selectedMsgIds.Contains(msgId);
            var mark = new Label
            {
                Text = sel ? "✔" : "○",
                AutoSize = false,
                Size = new Size(30, 30),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = sel ? Color.FromArgb(59, 165, 93) : Color.FromArgb(150, 152, 158),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(Math.Max(0, pnlMessages.ClientSize.Width - 46),
                    bubble.Top + Math.Max(0, (bubble.Height - 30) / 2))
            };
            int mid = msgId;
            mark.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleSelect(mid); };
            pnlMessages.Controls.Add(mark);
            mark.BringToFront();
            _selMark[msgId] = mark;   // для точечного обновления без перезагрузки чата
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТВЕТИТЬ
        // ════════════════════════════════════════════════════════════════
        private void BeginReply(int msgId, string senderName, string text)
        {
            CancelEdit();
            CancelForward();

            _replyToId = msgId;
            _replyToSender = senderName;
            _replyToText = text?.Length > 60 ? text[..60] + "…" : (text ?? "");

            _lblReplyInfo.Text = $"↩ Ответ для {senderName}: {_replyToText}";
            _pnlReplyBar.Visible = true;
            txtMessage.Focus();
        }

        private void CancelReply()
        {
            _replyToId = -1;
            _replyToText = "";
            _replyToSender = "";
            _pnlReplyBar.Visible = false;
        }

        // ── Вставляем reply_to_id при отправке — патч для SendMessage ────
        /// <summary>
        /// Дополнить INSERT в SendMessage и SendGroupMessage:
        /// если _replyToId > 0 — добавить reply_to_id=@rid в запрос.
        /// Вызывать после отправки.
        /// </summary>
        public void ApplyReplyToLastMessage(bool isGroup)
        {
            if (_replyToId < 0) return;
            try
            {
                int myId = UserSession.EffectiveId;
                using var conn = DBHelper.OpenConnection();
                string table = isGroup ? "group_messages" : "messages";
                // Обновляем последнее сообщение этого пользователя
                using var cmd = new MySqlCommand(
                    $"UPDATE {table} SET reply_to_id=@r " +
                    $"WHERE sender_id=@me ORDER BY id DESC LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@r", _replyToId);
                cmd.Parameters.AddWithValue("@me", myId);
                cmd.ExecuteNonQuery();
            }
            catch { }
            CancelReply();
        }

        // ════════════════════════════════════════════════════════════════
        //  ПЕРЕСЛАТЬ
        // ════════════════════════════════════════════════════════════════
        private void BeginForward(int msgId, string text, bool isGroup, string senderName = "")
        {
            CancelReply();
            CancelEdit();

            _forwardMsgId = msgId;
            _forwardText = text ?? "";
            _forwardSenderName = senderName ?? "";
            _forwardIsGroup = isGroup;
            _forwardSrcScope = isGroup ? 1 : 0;
            _forwardSrcId = msgId;         // для копии медиа SQL-ом

            string preview = text?.Length > 50 ? text[..50] + "…" : (text ?? "[медиа]");
            _lblForwardInfo.Text = $"↪ Пересылка от {senderName}: {preview}";
            _pnlForwardBar.Visible = true;

            MessageBox.Show(
                "Выберите диалог, группу или канал сервера и нажмите «Отправить».\n" +
                "Сообщение будет переслано туда.",
                "PISMO — Пересылка",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            txtMessage.Focus();
        }

        private void CancelForward()
        {
            _forwardMsgId = -1;
            _forwardText = "";
            _forwardSenderName = "";
            _forwardSrcScope = 0;
            _forwardSrcId = 0;
            _pnlForwardBar.Visible = false;
        }

        /// <summary>Есть ли что пересылать (одиночное или пачка выделенных).</summary>
        public bool HasPendingForward => _forwardMsgId >= 0 || _forwardBatch.Count > 0;

        /// <summary>Забирает ожидающие пересылки (отправитель, текст, откуда, id) и
        /// сбрасывает режим. Общая точка для ЛС, групп И серверов — получатель
        /// шлёт каждый элемент через ForwardHelper.Forward (медиа копируется SQL-ом).</summary>
        public System.Collections.Generic.List<(string sender, string text, int scope, int id)> ConsumePendingForwards()
        {
            var list = new System.Collections.Generic.List<(string sender, string text, int scope, int id)>();
            if (_forwardBatch.Count > 0)
            {
                list.AddRange(_forwardBatch);
                _forwardBatch.Clear();
                CancelForward();
            }
            else if (_forwardMsgId >= 0)
            {
                list.Add((_forwardSenderName, _forwardText, _forwardSrcScope, _forwardSrcId));
                CancelForward();
            }
            return list;
        }

        /// <summary>Пересылка, начатая ИЗ СЕРВЕРА: кладём в буфер (с id исходника —
        /// медиа тоже уедет). Дальше юзер выбирает диалог/группу/канал и жмёт «Отправить».</summary>
        public void BeginForwardExternal(string senderName, string text, int srcServerMsgId = 0)
        {
            BeginForward(0, text, false, senderName);
            _forwardSrcScope = 2;
            _forwardSrcId = srcServerMsgId;
        }

        /// <summary>Пачка сообщений из сервера (множественное выделение) — в общий
        /// буфер пересылки. Отправка: диалог/группа («Отправить» в ЛС) или канал.</summary>
        public void BeginForwardExternalBatch(System.Collections.Generic.List<(string sender, string text, int id)> batch)
        {
            if (batch == null || batch.Count == 0) return;
            CancelReply();
            CancelEdit();
            _forwardMsgId = -1;
            _forwardBatch.Clear();
            foreach (var (sndr, txt, id) in batch)
                _forwardBatch.Add((sndr, txt, 2, id));
            _lblForwardInfo.Text = $"↪ Пересылка {batch.Count} сообщений(я) — выберите диалог/группу/канал и нажмите «Отправить»";
            _pnlForwardBar.Visible = true;
        }

        public bool TrySendForward()
        {
            var pending = ConsumePendingForwards();
            if (pending.Count == 0) return false;

            bool toGroup = _currentGroupId >= 0;
            int target = toGroup ? _currentGroupId : _currentChatPartnerId;
            if (target < 0) return false;
            int me = UserSession.EffectiveId;

            foreach (var (sndr, txt, srcScope, srcId) in pending)
            {
                try { ForwardHelper.Forward(srcScope, srcId, sndr, txt, toGroup ? 1 : 0, me, target); }
                catch (Exception ex) { MessageBox.Show("Ошибка пересылки: " + ex.Message); return true; }
            }

            // Уведомляем получателя по WS и перерисовываем чат.
            try
            {
                if (toGroup) WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentGroupId, "group");
                // Адресат 0 — всем, как и в остальных местах: адресная доставка
                // обходила стороной второе устройство самого отправителя.
                else WebSocketSignalingClient.Instance.SendMessage("new_message", 0, _currentChatPartnerId, "direct");
            }
            catch { }
            ForceMessageRerender();
            if (toGroup) LoadGroupMessages(); else LoadMessages();
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  РЕДАКТИРОВАТЬ
        // ════════════════════════════════════════════════════════════════
        private void BeginEdit(int msgId, bool isGroup, string currentText)
        {
            CancelReply();
            CancelForward();

            _editMsgId = msgId;
            _isGroupEdit = isGroup;

            txtMessage.Text = currentText;
            _lblReplyInfo.Text = "✏ Режим редактирования — Enter для сохранения";
            _pnlReplyBar.Visible = true;

            txtMessage.Focus();
            txtMessage.SelectionStart = txtMessage.Text.Length;
        }

        private void CancelEdit()
        {
            if (_editMsgId < 0) return;
            _editMsgId = -1;
            txtMessage.Clear();
            _pnlReplyBar.Visible = false;
        }

        /// <summary>
        /// Вызывать из btnSend_Click ПЕРЕД основной отправкой.
        /// Если активен режим редактирования — сохраняет и возвращает true.
        /// </summary>
        /// <summary>Показывает историю изменений сообщения (2.0): прежние версии
        /// текста с датами. Пусто — сообщение не редактировалось.</summary>
        private void ShowEditHistory(int msgId, int scope)
        {
            var rows = new List<(string When, string Text)>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT old_text, edited_at FROM message_edits WHERE message_id=@m AND scope=@s ORDER BY edited_at DESC", conn);
                cmd.Parameters.AddWithValue("@m", msgId);
                cmd.Parameters.AddWithValue("@s", scope);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string when = r["edited_at"] == DBNull.Value ? "" : Convert.ToDateTime(r["edited_at"]).ToString("dd.MM.yyyy HH:mm");
                    string txt;
                    try { txt = Crypto.Dec(r["old_text"]?.ToString() ?? ""); } catch { txt = ""; }
                    rows.Add((when, txt));
                }
            }
            catch { }

            var pop = new Form
            {
                Text = "📝 История изменений",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(420, 400),
                BackColor = Color.FromArgb(49, 51, 56)
            };
            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Color.FromArgb(49, 51, 56), Padding = new Padding(10)
            };
            if (rows.Count == 0)
                list.Controls.Add(new Label { Text = "Это сообщение не редактировалось.", ForeColor = Color.FromArgb(150, 152, 158), AutoSize = true, Font = new Font("Segoe UI", 9.5f) });
            foreach (var (when, txt) in rows)
            {
                var card = new Panel { Width = 388, Height = 58, BackColor = Color.FromArgb(43, 45, 49), Margin = new Padding(0, 0, 0, 6) };
                card.Controls.Add(new Label { Text = "было · " + when, ForeColor = Color.FromArgb(150, 152, 158), AutoSize = false, Size = new Size(360, 15), Location = new Point(10, 6), Font = new Font("Segoe UI", 8f) });
                card.Controls.Add(new Label { Text = string.IsNullOrWhiteSpace(txt) ? "[пусто/вложение]" : (txt.Length > 120 ? txt[..120] + "…" : txt), ForeColor = Color.White, AutoSize = false, Size = new Size(368, 32), Location = new Point(10, 22), Font = new Font("Segoe UI", 9f), AutoEllipsis = true });
                list.Controls.Add(card);
            }
            pop.Controls.Add(list);
            pop.ShowDialog(this);
        }

        /// <summary>Шлёт собеседнику(ам) WS-сигнал «edit» — правка/удаление
        /// сообщения должны появляться у него сразу, а не после нового сообщения
        /// или переоткрытия чата. Для группы sessionId=groupId; для лички
        /// target=собеседник, sessionId=я.</summary>
        private void NotifyChatEdited(bool isGroup)
        {
            try
            {
                if (isGroup)
                    WebSocketSignalingClient.Instance.SendMessage("edit", 0, _currentGroupId, "group");
                else
                    WebSocketSignalingClient.Instance.SendMessage("edit", _currentChatPartnerId, UserSession.EffectiveId, "direct");
            }
            catch { }
        }

        public bool TrySaveEdit()
        {
            if (_editMsgId < 0) return false;

            string newText = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(newText)) return false;

            try
            {
                using var conn = DBHelper.OpenConnection();
                string table = _isGroupEdit ? "group_messages" : "messages";
                int scope = _isGroupEdit ? 1 : 0;

                // 2.0: сохраняем ПРЕЖНИЙ текст в историю изменений перед перезаписью.
                try
                {
                    string oldCipher = null;
                    using (var sel = new MySqlCommand($"SELECT text FROM {table} WHERE id=@id", conn))
                    {
                        sel.Parameters.AddWithValue("@id", _editMsgId);
                        oldCipher = sel.ExecuteScalar() as string;
                    }
                    if (oldCipher != null)
                    {
                        using var hist = new MySqlCommand(
                            "INSERT INTO message_edits (message_id, scope, old_text) VALUES (@m, @s, @o)", conn);
                        hist.Parameters.AddWithValue("@m", _editMsgId);
                        hist.Parameters.AddWithValue("@s", scope);
                        hist.Parameters.AddWithValue("@o", oldCipher);
                        hist.ExecuteNonQuery();
                    }
                }
                catch { /* история не критична для самого редактирования */ }

                using var cmd = new MySqlCommand(
                    $"UPDATE {table} SET text=@t, edited_at=NOW() WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@t", Crypto.Enc(newText));
                cmd.Parameters.AddWithValue("@id", _editMsgId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка редактирования: " + ex.Message);
                return false;
            }

            // Сообщаем собеседнику(ам) о правке — чтобы чат обновился в реальном
            // времени, а не только после нового сообщения/переоткрытия.
            NotifyChatEdited(_isGroupEdit);

            txtMessage.Clear();
            _editMsgId = -1;
            _pnlReplyBar.Visible = false;

            if (_isGroupEdit) LoadGroupMessages();
            else LoadMessages();

            return true;
        }

        // ════════════════════════════════════════════════════════════════
        //  УДАЛИТЬ СООБЩЕНИЕ
        // ════════════════════════════════════════════════════════════════
        private void DeleteMessage(int msgId, bool isGroup)
        {
            if (MessageBox.Show(
                "Удалить сообщение? Это действие нельзя отменить.",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                string table = isGroup ? "group_messages" : "messages";
                // Мягкое удаление — текст заменяем, флаг ставим
                using var cmd = new MySqlCommand(
                    $"UPDATE {table} SET is_deleted=1, text='[сообщение удалено]', " +
                    "image_data=NULL, audio_data=NULL, video_data=NULL " +
                    "WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", msgId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка удаления: " + ex.Message);
                return;
            }

            NotifyChatEdited(isGroup);
            if (isGroup) LoadGroupMessages();
            else LoadMessages();
        }

        // ════════════════════════════════════════════════════════════════
        //  УДАЛИТЬ ПЕРЕПИСКУ (раньше: только admin) — теперь: любой пользователь
        // ════════════════════════════════════════════════════════════════
        public void DeleteConversationWithPartner(int partnerId, string partnerName)
        {
            // Разрешаем пользователю очистить свою переписку с partnerId,
            // администратору — очистить переписку в контексте админских операций.
            if (MessageBox.Show(
                $"Удалить ВСЮ переписку с {partnerName}?\nЭто действие нельзя отменить.",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;

            int myId = UserSession.EffectiveId;
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Двумя запросами вместо OR: удаление по индексу, а не сканом всей
                // таблицы (в ней лежат вложения — скан дорогой).
                foreach (var sql in new[]
                {
                    "DELETE FROM messages WHERE sender_id=@me AND receiver_id=@them",
                    "DELETE FROM messages WHERE sender_id=@them AND receiver_id=@me"
                })
                {
                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@me", myId);
                    cmd.Parameters.AddWithValue("@them", partnerId);
                    cmd.CommandTimeout = 600;
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка: " + ex.Message);
                return;
            }

            ClearChat();
            if (UserSession.Role == "admin" && !UserSession.IsImpersonating)
                LoadAllUsersForAdmin();
            else
                LoadConversations();
        }

        public void DeleteGroup(int groupId, string groupName)
        {
            // Проверяем — только создатель или admin
            bool isCreator = false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT created_by FROM group_chats WHERE id=@g", conn);
                cmd.Parameters.AddWithValue("@g", groupId);
                var obj = cmd.ExecuteScalar();
                if (obj != null && Convert.ToInt32(obj) == UserSession.EffectiveId)
                    isCreator = true;
            }
            catch { }

            if (!isCreator && UserSession.Role != "admin")
            {
                MessageBox.Show("Удалять группу может только её создатель или администратор системы.");
                return;
            }

            if (MessageBox.Show(
                $"Удалить группу «{groupName}» и все её сообщения?\nЭто нельзя отменить.",
                "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                // Каскадное удаление если FK настроены, иначе вручную
                new MySqlCommand($"DELETE FROM group_messages WHERE group_id={groupId}", conn)
                    .ExecuteNonQuery();
                new MySqlCommand($"DELETE FROM group_members WHERE group_id={groupId}", conn)
                    .ExecuteNonQuery();
                new MySqlCommand($"DELETE FROM group_chats WHERE id={groupId}", conn)
                    .ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка удаления группы: " + ex.Message);
                return;
            }

            ClearChat();
            LoadGroups();
            MessageBox.Show($"Группа «{groupName}» удалена.", "PISMO",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТОБРАЖЕНИЕ ОТВЕТА В ПУЗЫРЕ (вызывать из BuildBubble)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Строит мини-цитату ответа внутри пузыря.
        /// Возвращает высоту добавленного блока.
        /// </summary>
        /// <summary>
        /// Цитаты ответов для показываемой страницы: id сообщения → текст и автор.
        /// Заполняется PreloadQuotes перед отрисовкой, очищается после.
        /// </summary>
        private Dictionary<int, (string text, string sender)> _quotesInView;

        /// <summary>
        /// Забирает ВСЕ цитаты страницы одним запросом.
        ///
        /// Без этого каждое сообщение-ответ тянуло свою цитату отдельно, прямо
        /// в цикле отрисовки и на потоке интерфейса. Пока база стояла рядом, это
        /// терялось в шуме; на удалённом сервере сорок сообщений превращались в
        /// сорок подключений подряд.
        /// </summary>
        public void PreloadQuotes(DataTable dt, bool isGroup)
            => _quotesInView = BuildQuotesMap(dt, isGroup);

        /// <summary>Цитаты страницы одним запросом. Ничего не присваивает —
        /// чтобы вызывать её можно было из фонового потока, вместе с другими
        /// запросами страницы.</summary>
        public Dictionary<int, (string text, string sender)> BuildQuotesMap(DataTable dt, bool isGroup)
        {
            if (dt == null || dt.Rows.Count == 0) return null;
            if (!dt.Columns.Contains("reply_to_id")) return null;

            var ids = new List<int>();
            foreach (DataRow r in dt.Rows)
            {
                if (r["reply_to_id"] == DBNull.Value) continue;
                int id = Convert.ToInt32(r["reply_to_id"]);
                if (id > 0 && !ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) return null;

            var map = new Dictionary<int, (string text, string sender)>();
            try
            {
                string table = isGroup ? "group_messages" : "messages";
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand($@"
                    SELECT m.id, m.text, TRIM(CONCAT(u.Name,' ',u.Surname)) AS sname, u.login
                    FROM {table} m
                    JOIN users u ON u.id = m.sender_id
                    WHERE m.id IN ({string.Join(",", ids)})", conn);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    int id = Convert.ToInt32(rd["id"]);
                    string text = "";
                    try { text = Crypto.Dec(rd["text"] == DBNull.Value ? "" : rd["text"].ToString()); } catch { }
                    string sender = rd["sname"] == DBNull.Value ? "" : rd["sname"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(sender))
                        sender = rd["login"] == DBNull.Value ? "" : rd["login"].ToString();
                    map[id] = (text, sender);
                }
            }
            catch { }
            return map;
        }

        public int BuildReplyQuote(Panel bubble, int replyToId, bool isGroup,
            bool isMine, int startY, int innerW, int pad)
        {
            if (replyToId <= 0) return 0;

            try
            {
                string qText = "";
                string qSender = "";

                // Цитаты на всю страницу забраны заранее, одним запросом
                // (PreloadQuotes). Раньше здесь стоял отдельный поход в базу НА
                // КАЖДОЕ сообщение с ответом, и на удалённом сервере это стоило
                // 120 мс на подключение плюс сам запрос — по замеру отрисовка
                // страницы занимала восемь секунд, а перерисовка шестнадцать.
                if (_quotesInView != null && _quotesInView.TryGetValue(replyToId, out var q))
                {
                    qText = q.text;
                    qSender = q.sender;
                }
                else if (_drawingPage)
                {
                    // Идёт отрисовка страницы, а карта цитат ещё не готова (первое
                    // открытие чата — её наполняет фоновая выборка). Поштучно НЕ
                    // тянем: это ровно тот запрос на каждое сообщение, из-за которого
                    // первая отрисовка стоила секунды. Цитата появится на следующей
                    // перерисовке, когда карта придёт.
                    return 0;
                }
                else
                {
                    // Запасной путь: цитата понадобилась вне отрисовки страницы.
                    using var conn = DBHelper.OpenConnection();
                    string table = isGroup ? "group_messages" : "messages";
                    string sql = $@"
                        SELECT m.text, TRIM(CONCAT(u.Name,' ',u.Surname)) AS sname, u.login
                        FROM {table} m
                        JOIN users u ON u.id = m.sender_id
                        WHERE m.id = @id";
                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@id", replyToId);
                    var dt = new DataTable();
                    new MySqlDataAdapter(cmd).Fill(dt);

                    if (dt.Rows.Count > 0)
                    {
                        qText = Crypto.Dec(dt.Rows[0]["text"].ToString());
                        qSender = dt.Rows[0]["sname"].ToString().Trim();
                        if (string.IsNullOrWhiteSpace(qSender))
                            qSender = dt.Rows[0]["login"].ToString();
                    }
                }

                if (string.IsNullOrEmpty(qText)) return 0;

                // Полоска цитаты
                var stripe = new Panel
                {
                    BackColor = Color.FromArgb(0, 176, 244),
                    Size = new Size(3, 40),
                    Location = new Point(pad, startY)
                };

                var lblQ = new Label
                {
                    Text = $"{qSender}: {(qText.Length > 50 ? qText[..50] + "…" : qText)}",
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = Color.FromArgb(0, 176, 244),
                    AutoSize = false,
                    Size = new Size(innerW - 8, 38),
                    Location = new Point(pad + 6, startY + 2),
                    Padding = new Padding(0)
                };

                bubble.Controls.Add(stripe);
                bubble.Controls.Add(lblQ);

                return 46;
            }
            catch { return 0; }
        }

        // ════════════════════════════════════════════════════════════════
        //  ЗВОНКИ
        // ════════════════════════════════════════════════════════════════
        private void StartCall(bool withVideo)
        {
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Выберите собеседника или группу для звонка.");
                return;
            }
            if (_activeCall != null && !_activeCall.IsDisposed)
            {
                _activeCall.Activate();
                return;
            }
            // В серверном голосовом канале (или другом окне звонка)? Выходим —
            // нельзя быть в двух ГС разом.
            try { if (HasActiveVoice()) EndCurrentVoice(); } catch { }

            int myId = UserSession.EffectiveId;
            int peerId = _currentChatPartnerId;
            string targetName = _currentGroupId >= 0 ? _currentGroupName : _currentChatPartnerName;

            int existingSessionId = -1;

            try
            {
                using var conn = DBHelper.OpenConnection();
                // Проверяем, есть ли уже активный звонок в этой группе или с этим пользователем
                string qCheck = _currentGroupId >= 0
                    ? "SELECT id FROM call_sessions WHERE group_id=@gid AND status IN ('ringing','active') ORDER BY id DESC LIMIT 1"
                    : "SELECT id FROM call_sessions WHERE ((caller_id=@me AND callee_id=@peer) OR (caller_id=@peer AND callee_id=@me)) AND status IN ('ringing','active') ORDER BY id DESC LIMIT 1";

                using var cmdCheck = new MySqlCommand(qCheck, conn);
                if (_currentGroupId >= 0) cmdCheck.Parameters.AddWithValue("@gid", _currentGroupId);
                else
                {
                    cmdCheck.Parameters.AddWithValue("@me", myId);
                    cmdCheck.Parameters.AddWithValue("@peer", peerId);
                }

                var obj = cmdCheck.ExecuteScalar();
                if (obj != null && obj != DBNull.Value)
                {
                    existingSessionId = Convert.ToInt32(obj);
                }
            }
            catch { }

            if (existingSessionId > 0)
            {
                // Заходим в существующий звонок (обратный заход / присоединение к групповому звонку)
                _activeCall = new CallForm(existingSessionId, isCaller: false, peerName: targetName, peerId: peerId, hasVideo: withVideo, groupId: _currentGroupId);
                _activeCall.FormClosed += (s, e) => { _activeCall = null; HideVoiceDock(); };
                _activeCall.Show(this);
                ShowVoiceDock(targetName);
                return;
            }

            // Иначе создаем новый звонок
            int sessionId = -1;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT INTO call_sessions (caller_id, callee_id, group_id, status, has_video) " +
                    "VALUES (@c, @e, @g, 'ringing', @v)", conn);
                cmd.Parameters.AddWithValue("@c", myId);
                cmd.Parameters.AddWithValue("@e", peerId > 0 ? peerId : DBNull.Value);
                cmd.Parameters.AddWithValue("@g", _currentGroupId >= 0 ? _currentGroupId : DBNull.Value);
                cmd.Parameters.AddWithValue("@v", withVideo ? 1 : 0);

                cmd.ExecuteNonQuery();
                sessionId = Convert.ToInt32(cmd.LastInsertedId);
                if (sessionId == 0)
                {
                    using var rcmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn);
                    var obj = rcmd.ExecuteScalar();
                    if (obj != null) sessionId = Convert.ToInt32(obj);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка создания звонка: " + ex.Message);
                return;
            }

            if (sessionId <= 0) return;

            _activeCall = new CallForm(sessionId, isCaller: true, peerName: targetName, peerId: peerId, hasVideo: withVideo, groupId: _currentGroupId);
            _activeCall.FormClosed += (s, e) => { _activeCall = null; HideVoiceDock(); };
            _activeCall.Show(this);
            ShowVoiceDock(targetName);

            if (_currentGroupId >= 0)
                WebSocketSignalingClient.Instance.SendMessage("incoming_call", 0, sessionId, "group");
            else
                WebSocketSignalingClient.Instance.SendMessage("incoming_call", peerId, sessionId, withVideo ? "video" : "audio");
        }

        // ── Polling входящих звонков (добавить в PollTick) ────────────────
        private void HookCallPolling()
        {
            // PollTick уже существует — просто добавим вызов CheckIncomingCalls
            // через отдельный таймер чтобы не менять оригинал
            var callPollTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            callPollTimer.Tick += (s, e) => CheckIncomingCalls();
            callPollTimer.Start();

            WebSocketSignalingClient.Instance.OnMessageReceived += (type, senderId, sessionId, payload) =>
            {
                if (type == "incoming_call")
                {
                    try { BeginInvoke(() => { _pushedCheck = true; CheckIncomingCalls(); }); } catch { }
                }
            };
        }

        // Звонки, по которым уже показали окно входящего — чтобы не дёргать
        // повторно на каждом опросе. Заменяет ненадёжный фильтр "id > last",
        // из-за которого часть звонков пропускалась ("не всегда приходит").
        private readonly System.Collections.Generic.HashSet<int> _shownCallIds = new();

        private bool _callPollBusy;   // запрос уже идёт — не наслаиваем
        private int _callPollSkip;    // прореживание опроса при живом WS

        private void CheckIncomingCalls()
        {
            if (_activeCall != null && !_activeCall.IsDisposed) return;
            if (_callPollBusy) return;

            // При здоровом WS входящий звонок приходит push'ем (incoming_call →
            // мгновенный CheckIncomingCalls), опрос БД — лишь страховка, поэтому
            // прореживаем его до ~6 c. Без WS — полный темп (1.5 c).
            bool pushed = _pushedCheck; _pushedCheck = false;
            if (!pushed && WebSocketSignalingClient.Instance.IsHealthy && (++_callPollSkip % 4) != 0) return;

            _callPollBusy = true;
            int myId = UserSession.EffectiveId;

            // ВАЖНО: запрос к БД — в фоне. Раньше он шёл каждые 1.5 с прямо на
            // UI-потоке (JOIN по call_sessions) — это и было «подлагивание раз
            // в ~3 секунды», особенно с удалённой БД.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var dt = new DataTable();
                    using (var conn = DBHelper.OpenConnection())
                    {
                        // Берём ВСЕ звонки в статусе ringing, адресованные мне (личные
                        // по callee_id или групповые по членству), кроме своих же.
                        // Фильтрация «уже показанных» — на клиенте через _shownCallIds.
                        const string sql = @"
                            SELECT cs.id, cs.caller_id, cs.has_video, cs.group_id,
                                   TRIM(CONCAT(u.Name,' ',u.Surname)) AS caller_name, u.login
                            FROM call_sessions cs
                            JOIN users u ON u.id = cs.caller_id
                            LEFT JOIN group_members gm ON gm.group_id = cs.group_id AND gm.user_id = @me
                            WHERE (cs.callee_id = @me OR gm.user_id = @me)
                              AND cs.status = 'ringing'
                              AND cs.caller_id != @me
                            ORDER BY cs.id ASC";
                        using var cmd = new MySqlCommand(sql, conn);
                        cmd.Parameters.AddWithValue("@me", myId);
                        new MySqlDataAdapter(cmd).Fill(dt);
                    }

                    if (dt.Rows.Count == 0 || IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() => ShowIncomingFromRows(dt)));
                }
                catch { }
                finally { _callPollBusy = false; }
            });
        }

        private bool _pushedCheck;   // проверка вызвана WS-событием — без прореживания

        /// <summary>UI-часть: показать окно входящего звонка по строкам опроса.</summary>
        private void ShowIncomingFromRows(DataTable dt)
        {
            if (_activeCall != null && !_activeCall.IsDisposed) return;
            try
            {
                foreach (DataRow row in dt.Rows)
                {
                    int sid = Convert.ToInt32(row["id"]);
                    if (!_shownCallIds.Add(sid)) continue; // уже показывали это окно

                    int callerId = Convert.ToInt32(row["caller_id"]);
                    bool hasVid = Convert.ToBoolean(row["has_video"]);
                    int groupId = row["group_id"] == DBNull.Value ? -1 : Convert.ToInt32(row["group_id"]);
                    string cname = row["caller_name"].ToString().Trim();
                    if (string.IsNullOrWhiteSpace(cname)) cname = row["login"].ToString();

                    // Игнорируемый собеседник: звонок не показываем и не звеним.
                    // Сессию не отклоняем — у звонящего просто идут гудки, как
                    // если бы нас не было на месте.
                    if (ChatMutes.IsMuted(callerId)) return;

                    // Отдельный запрет «не принимать звонки»: сообщения от человека
                    // приходят как обычно, а вызов не показываем и не звеним. Для
                    // группового звонка запрет ставится на саму группу.
                    if (CallBlocks.IsBlocked(callerId)) return;
                    if (groupId > 0 && CallBlocks.IsBlocked(CallBlocks.GroupKey(groupId))) return;

                    // Показываем входящий звонок
                    var incoming = new IncomingCallForm(sid, cname, callerId);
                    incoming.FormClosed += (s, e) =>
                    {
                        if (incoming.Accepted)
                        {
                            incoming.Dispose();
                            // Обновляем статус в БД
                            try
                            {
                                using var c2 = DBHelper.OpenConnection();
                                using var u2 = new MySqlCommand(
                                    "UPDATE call_sessions SET status='active', answered_at=NOW() WHERE id=@id", c2);
                                u2.Parameters.AddWithValue("@id", sid);
                                u2.ExecuteNonQuery();
                                WebSocketSignalingClient.Instance.SendMessage("call_status", callerId, sid, "active");
                                // И СВОИМ остальным устройствам: там сейчас звонит
                                // такой же входящий, и без этого он продолжал бы
                                // звонить после того, как трубку уже взяли здесь.
                                WebSocketSignalingClient.Instance.SendMessage(
                                    "call_status", UserSession.EffectiveId, sid, "active");
                            }
                            catch { }

                            _activeCall = new CallForm(sid, isCaller: false,
                                peerName: cname, peerId: callerId, hasVideo: hasVid, groupId: groupId);
                            _activeCall.FormClosed += (s2, e2) => { _activeCall = null; HideVoiceDock(); };
                            _activeCall.Show(this);
                            ShowVoiceDock(cname);
                        }
                        else
                        {
                            incoming.Dispose();
                        }
                    };
                    incoming.Show();
                    break; // Один звонок за раз
                }
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════
        //  КОНТЕКСТНОЕ МЕНЮ САЙДБАРА (переписка / группа)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Добавить контекстное меню на карточку диалога в сайдбаре.
        /// Вызывать из AddUserCard.
        /// </summary>
        public void AttachConversationContextMenu(Panel card, int partnerId, string partnerName)
        {
            // ПКМ-меню вешаем не только на саму карточку, но и на все её дочерние
            // контролы (аватар, имя, превью) — иначе клик правой по тексту/аватару
            // «проваливался» мимо меню и вместо него просто открывался чат.
            void Handler(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                var menu = new ContextMenuStrip();
                menu.BackColor = Color.FromArgb(24, 25, 28);
                menu.ForeColor = Color.FromArgb(220, 221, 222);

                var itemProfile = new ToolStripMenuItem("👤 Профиль");
                itemProfile.Click += (s2, e2) =>
                {
                    using var pf = new ProfileForm(partnerId, readOnly: true);
                    pf.ShowDialog(this);
                };
                menu.Items.Add(itemProfile);

                // Друзья (только для обычных пользователей — это меню и так вешается
                // на карточки пользователей, не групп).
                try
                {
                    var rel = FriendsRepository.GetRelation(UserSession.EffectiveId, partnerId);
                    string caption = rel switch
                    {
                        FriendsRepository.Relation.Friend => "➖ Удалить из друзей",
                        FriendsRepository.Relation.OutgoingPending => "⏳ Отменить заявку",
                        FriendsRepository.Relation.IncomingPending => "✔ Принять заявку в друзья",
                        _ => "📨 Отправить заявку в друзья"
                    };
                    var itemFriend = new ToolStripMenuItem(caption);
                    itemFriend.Click += (s2, e2) =>
                    {
                        switch (rel)
                        {
                            case FriendsRepository.Relation.Friend:
                            case FriendsRepository.Relation.OutgoingPending:
                                FriendsRepository.Remove(UserSession.EffectiveId, partnerId); break;
                            case FriendsRepository.Relation.IncomingPending:
                                FriendsRepository.AcceptRequest(UserSession.EffectiveId, partnerId); break;
                            default:
                                FriendsRepository.SendRequest(UserSession.EffectiveId, partnerId); break;
                        }
                        try { if (UserSession.Role == "admin" && !UserSession.IsImpersonating) LoadAllUsersForAdmin(); else LoadConversations(); } catch { }
                    };
                    menu.Items.Add(itemFriend);
                }
                catch { }

                var itemCall = new ToolStripMenuItem("📞 Позвонить");
                itemCall.Click += (s2, e2) =>
                {
                    OpenChat(partnerId, partnerName);
                    StartCall(withVideo: false);
                };
                menu.Items.Add(itemCall);

                // Закрепление чата (2.1): диалог прижимается к верху списка ЛС
                // (ниже групп) независимо от свежести переписки. Хранится локально.
                try
                {
                    bool chatPinned = ChatPins.IsPinned(partnerId);
                    var itemPinChat = new ToolStripMenuItem(chatPinned ? "📌 Открепить чат" : "📌 Закрепить чат");
                    itemPinChat.Click += (s2, e2) =>
                    {
                        ChatPins.Toggle(partnerId);
                        try
                        {
                            if (UserSession.Role == "admin" && !UserSession.IsImpersonating) LoadAllUsersForAdmin();
                            else LoadConversations();
                        }
                        catch { }
                    };
                    menu.Items.Add(itemPinChat);
                }
                catch { }

                // Не принимать звонки от этого человека (локально). Отдельно от
                // «игнорировать»: сообщения продолжают приходить, молчит только вызов.
                try
                {
                    bool noCalls = CallBlocks.IsBlocked(partnerId);
                    var itemCalls = new ToolStripMenuItem(
                        noCalls ? "📞 Принимать звонки" : "🚫 Не принимать звонки");
                    itemCalls.Click += (s2, e2) => CallBlocks.Toggle(partnerId);
                    menu.Items.Add(itemCalls);
                }
                catch { }

                // Игнорирование собеседника (локально): от него не приходят ни
                // уведомления о сообщениях, ни входящие звонки.
                try
                {
                    bool muted = ChatMutes.IsMuted(partnerId);
                    var itemMute = new ToolStripMenuItem(
                        muted ? "🔔 Больше не игнорировать" : "🔕 Игнорировать");
                    itemMute.Click += (s2, e2) =>
                    {
                        ChatMutes.Toggle(partnerId);
                        try
                        {
                            if (UserSession.Role == "admin" && !UserSession.IsImpersonating) LoadAllUsersForAdmin();
                            else LoadConversations();
                        }
                        catch { }
                    };
                    menu.Items.Add(itemMute);
                }
                catch { }

                // Пометить прочитанным ИМЕННО этот диалог — без захода в чат.
                var itemRead = new ToolStripMenuItem("✓ Пометить прочитанным");
                itemRead.Click += (s2, e2) =>
                {
                    int me = UserSession.EffectiveId;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            using var cmd = new MySqlCommand(
                                "UPDATE messages SET is_read=1 WHERE sender_id=@p AND receiver_id=@me AND is_read=0", conn);
                            cmd.Parameters.AddWithValue("@p", partnerId);
                            cmd.Parameters.AddWithValue("@me", me);
                            int n = cmd.ExecuteNonQuery();
                            // Отправителю — событие «read», чтобы его галочки посинели сразу.
                            if (n > 0)
                                try { WebSocketSignalingClient.Instance.SendMessage("read", partnerId, me, "direct"); } catch { }
                        }
                        catch { }
                        if (IsDisposed || !IsHandleCreated) return;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (UserSession.Role == "admin" && !UserSession.IsImpersonating) LoadAllUsersForAdmin();
                                    else LoadConversations();
                                    PollTick(null, null);
                                }
                                catch { }
                            }));
                        }
                        catch { }
                    });
                };
                menu.Items.Add(itemRead);

                // Блок/разблок пользователя
                try
                {
                    bool amIBlocked = IsUserBlocked(UserSession.EffectiveId, partnerId);
                    // Если я уже заблокировал партнёра — показать "Разблокировать"
                    menu.Items.Add(new ToolStripSeparator());
                    var itemBlock = new ToolStripMenuItem(
                        amIBlocked ? "✅ Разблокировать пользователя" : "🚫 Заблокировать пользователя");
                    itemBlock.Click += (s2, e2) =>
                    {
                        if (amIBlocked)
                        {
                            UnblockUser(UserSession.EffectiveId, partnerId);
                            MessageBox.Show($"Пользователь {partnerName} разблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            BlockUser(UserSession.EffectiveId, partnerId);
                            MessageBox.Show($"Пользователь {partnerName} заблокирован.", "PISMO",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    };
                    menu.Items.Add(itemBlock);
                }
                catch
                {
                    // Игнорируем ошибки проверки блокировки
                }

                // Очистить переписку (для себя) / или админ — для всех
                menu.Items.Add(new ToolStripSeparator());
                var itemClear = new ToolStripMenuItem("🗑 Очистить переписку");
                itemClear.Click += (s2, e2) =>
                {
                    // Используем тот же метод — он удаляет записи между мной и партнёром
                    DeleteConversationWithPartner(partnerId, partnerName);
                };
                menu.Items.Add(itemClear);

                if (UserSession.Role == "admin")
                {
                    menu.Items.Add(new ToolStripSeparator());
                    var itemDel = new ToolStripMenuItem("🗑 Удалить переписку (ADMIN)")
                    { ForeColor = Color.FromArgb(240, 71, 71) };
                    itemDel.Click += (s2, e2) =>
                        DeleteConversationWithPartner(partnerId, partnerName);
                    menu.Items.Add(itemDel);
                }

                menu.Show(Cursor.Position);
            }

            card.MouseClick += Handler;
            void Wire(Control parent)
            {
                foreach (Control c in parent.Controls)
                {
                    c.MouseClick += Handler;
                    if (c.HasChildren) Wire(c);
                }
            }
            Wire(card);
        }

        /// <summary>
        /// Добавить контекстное меню на карточку группы в сайдбаре.
        /// </summary>
        public void AttachGroupContextMenu(Panel card, int groupId, string groupName)
        {
            card.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var menu = new ContextMenuStrip();
                menu.BackColor = Color.FromArgb(24, 25, 28);
                menu.ForeColor = Color.FromArgb(220, 221, 222);

                var itemDel = new ToolStripMenuItem("🗑 Удалить группу")
                { ForeColor = Color.FromArgb(240, 71, 71) };
                itemDel.Click += (s2, e2) => DeleteGroup(groupId, groupName);
                menu.Items.Add(itemDel);

                menu.Show(Cursor.Position);
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  БЛОКИРОВАНИЕ ПОЛЬЗОВАТЕЛЕЙ (простая реализация через таблицу user_blocks)
        //  Таблица (если нет) — можно создать вручную:
        //  CREATE TABLE IF NOT EXISTS user_blocks (
        //      id INT AUTO_INCREMENT PRIMARY KEY,
        //      blocker_id INT NOT NULL,
        //      blocked_id INT NOT NULL,
        //      created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
        //      UNIQUE KEY ux_block (blocker_id, blocked_id)
        //  );
        // ════════════════════════════════════════════════════════════════

        private void BlockUser(int blockerId, int blockedId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                // Создадим таблицу при необходимости (безопасно)
                using var create = new MySqlCommand(@"
                    CREATE TABLE IF NOT EXISTS user_blocks (
                        id INT AUTO_INCREMENT PRIMARY KEY,
                        blocker_id INT NOT NULL,
                        blocked_id INT NOT NULL,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        UNIQUE KEY ux_block (blocker_id, blocked_id)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", conn);
                create.ExecuteNonQuery();

                using var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO user_blocks (blocker_id, blocked_id) VALUES (@b, @t)", conn);
                cmd.Parameters.AddWithValue("@b", blockerId);
                cmd.Parameters.AddWithValue("@t", blockedId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BLOCK] Ошибка блокировки: " + ex.Message);
                MessageBox.Show("Ошибка при блокировке пользователя: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UnblockUser(int blockerId, int blockedId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM user_blocks WHERE blocker_id=@b AND blocked_id=@t", conn);
                cmd.Parameters.AddWithValue("@b", blockerId);
                cmd.Parameters.AddWithValue("@t", blockedId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BLOCK] Ошибка разблокировки: " + ex.Message);
                MessageBox.Show("Ошибка при разблокировке пользователя: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Обе стороны блокировки за ОДИН запрос: я заблокировал собеседника и
        /// он заблокировал меня. Раньше это были два отдельных IsUserBlocked, то
        /// есть два подключения подряд — по замеру 368 мс при открытии чата и
        /// столько же перед каждой отправкой сообщения.
        /// </summary>
        private (bool iBlocked, bool theyBlocked) BlockStateBoth(int me, int them)
        {
            bool iB = false, tB = false;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT blocker_id FROM user_blocks " +
                    "WHERE (blocker_id=@me AND blocked_id=@them) " +
                    "   OR (blocker_id=@them AND blocked_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    int blocker = Convert.ToInt32(rd[0]);
                    if (blocker == me) iB = true;
                    else if (blocker == them) tB = true;
                }
            }
            catch { }
            return (iB, tB);
        }

        private bool IsUserBlocked(int blockerId, int blockedId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM user_blocks WHERE blocker_id=@b AND blocked_id=@t LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@b", blockerId);
                cmd.Parameters.AddWithValue("@t", blockedId);
                var obj = cmd.ExecuteScalar();
                return obj != null;
            }
            catch
            {
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  ОТОБРАЖЕНИЕ ОТВЕТА В ПУЗЫРЕ (повтор)
        // ════════════════════════════════════════════════════════════════
        // (остальной код файла без изменений)
    }
}
