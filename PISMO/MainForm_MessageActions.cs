// ============================================================
//  MainForm — MessageActions + CallSupport (partial)
//  Добавьте этот файл в проект рядом с MainForm.cs
//  Это partial class — расширяет MainForm без правки оригинала
// ============================================================
using System;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

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

        // ════════════════════════════════════════════════════════════════
        //  ИНИЦИАЛИЗАЦИЯ (вызывать из MainForm_Load или конструктора)
        // ════════════════════════════════════════════════════════════════
        public void InitMessageActions()
        {
            BuildReplyBar();
            BuildForwardBar();
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
        public void AttachBubbleContextMenu(Panel bubble, int msgId, bool isGroup,
    bool isMine, string text, string senderName)
        {
            var menu = new ContextMenuStrip();
            menu.BackColor = Color.FromArgb(24, 25, 28);
            menu.ForeColor = Color.FromArgb(220, 221, 222);
            menu.Font = new Font("Segoe UI", 9.5f);

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

            // ── Удалить сообщение (своё или admin) ───────────────────────
            bool canDelete = isMine || UserSession.Role == "admin";
            if (canDelete)
            {
                var itemDel = new ToolStripMenuItem("🗑  Удалить сообщение")
                { ForeColor = Color.FromArgb(240, 71, 71) };
                itemDel.Click += (s, e) => DeleteMessage(msgId, isGroup);
                menu.Items.Add(itemDel);
            }

            // Правый клик по пузырю и всем его дочерним контролам
            void ShowMenu(object? s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                    menu.Show(Cursor.Position);
            }

            bubble.MouseClick += ShowMenu;
            foreach (Control c in bubble.Controls)
            {
                // У выделяемого TextBox правый клик иначе показал бы родное меню —
                // подменяем нашим (в нём есть «Копировать» с учётом выделения).
                if (c is TextBox tb) tb.ContextMenuStrip = menu;
                else c.MouseClick += ShowMenu;
            }
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

            string preview = text?.Length > 50 ? text[..50] + "…" : (text ?? "[медиа]");
            _lblForwardInfo.Text = $"↪ Пересылка от {senderName}: {preview}";
            _pnlForwardBar.Visible = true;

            MessageBox.Show(
                "Выберите диалог или группу в сайдбаре и нажмите «Отправить».\n" +
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
            _pnlForwardBar.Visible = false;
        }

        public bool TrySendForward()
        {
            if (_forwardMsgId < 0) return false;

            // Формируем текст с указанием отправителя
            string sender = _forwardSenderName;
            string textToSend = string.IsNullOrWhiteSpace(sender)
                ? $"↪ Переслано:\n{_forwardText}"
                : $"↪ Переслано от {sender}:\n{_forwardText}";

            CancelForward();

            if (_currentGroupId >= 0)
                SendGroupMessage(textToSend, null);
            else if (_currentChatPartnerId >= 0)
                SendMessage(textToSend, null);

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
        public bool TrySaveEdit()
        {
            if (_editMsgId < 0) return false;

            string newText = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(newText)) return false;

            try
            {
                using var conn = DBHelper.OpenConnection();
                string table = _isGroupEdit ? "group_messages" : "messages";
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
                using var cmd = new MySqlCommand(
                    "DELETE FROM messages " +
                    "WHERE (sender_id=@me AND receiver_id=@them) " +
                    "   OR (sender_id=@them AND receiver_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", myId);
                cmd.Parameters.AddWithValue("@them", partnerId);
                cmd.ExecuteNonQuery();
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
        public int BuildReplyQuote(Panel bubble, int replyToId, bool isGroup,
            bool isMine, int startY, int innerW, int pad)
        {
            if (replyToId <= 0) return 0;

            try
            {
                string qText = "";
                string qSender = "";

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
