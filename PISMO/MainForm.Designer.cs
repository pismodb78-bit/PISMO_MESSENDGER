namespace PISMO
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        // Сайдбар
        private Panel pnlSidebar;
        private Panel pnlSidebarHeader;
        private Label lblSidebarTitle;
        private FlowLayoutPanel pnlUserList;
        private Panel pnlSidebarFooter;
        private Panel pnlMyAvatar;
        private Label lblCurrentUser;
        private Button btnSettings;
        private Button btnRefresh;
        private Button btnNewGroup;

        // Основная область
        private Panel pnlMain;
        private Panel pnlChatHeader;
        private Label lblChatTitle;
        private Panel pnlMessages;
        private Panel pnlPreview;
        private Panel pnlInputBar;
        private Button btnAttach;
        private Button btnVoice;
        private Button btnVideoCircle;
        private Button btnSend;

        // Трей-иконка для уведомлений
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                components?.Dispose();
                _trayIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            pnlSidebar = new Panel();
            pnlUserList = new FlowLayoutPanel();
            pnlSidebarHeader = new Panel();
            lblSidebarTitle = new Label();
            btnNewGroup = new Button();
            btnRefresh = new Button();
            pnlSidebarFooter = new Panel();
            pnlMyAvatar = new Panel();
            lblCurrentUser = new Label();
            btnSettings = new Button();
            pnlMain = new Panel();
            pnlMessages = new Panel();
            pnlPreview = new Panel();
            pnlInputBar = new Panel();
            btnAttach = new Button();
            btnVoice = new Button();
            btnVideoCircle = new Button();
            txtMessage = new TextBox();
            btnSend = new Button();
            pnlChatHeader = new Panel();
            lblChatTitle = new Label();
            _trayMenu = new ContextMenuStrip(components);
            _trayIcon = new NotifyIcon(components);
            pnlSidebar.SuspendLayout();
            pnlSidebarHeader.SuspendLayout();
            pnlSidebarFooter.SuspendLayout();
            pnlMain.SuspendLayout();
            pnlInputBar.SuspendLayout();
            pnlChatHeader.SuspendLayout();
            SuspendLayout();
            // 
            // pnlSidebar
            // 
            pnlSidebar.BackColor = Color.FromArgb(32, 34, 37);
            pnlSidebar.Controls.Add(pnlUserList);
            pnlSidebar.Controls.Add(pnlSidebarHeader);
            pnlSidebar.Controls.Add(pnlSidebarFooter);
            pnlSidebar.Dock = DockStyle.Left;
            pnlSidebar.Location = new Point(0, 0);
            pnlSidebar.Name = "pnlSidebar";
            pnlSidebar.Size = new Size(264, 700);   // список DM чуть шире
            pnlSidebar.TabIndex = 2;
            // 
            // pnlUserList
            // 
            pnlUserList.AutoScroll = true;
            pnlUserList.BackColor = Color.FromArgb(32, 34, 37);
            pnlUserList.Dock = DockStyle.Fill;
            pnlUserList.FlowDirection = FlowDirection.TopDown;
            pnlUserList.Location = new Point(0, 48);
            pnlUserList.Name = "pnlUserList";
            pnlUserList.Padding = new Padding(4, 6, 4, 6);
            pnlUserList.Size = new Size(250, 600);
            pnlUserList.TabIndex = 0;
            pnlUserList.WrapContents = false;
            pnlUserList.Resize += pnlUserList_Resize;
            // 
            // pnlSidebarHeader
            // 
            pnlSidebarHeader.BackColor = Color.FromArgb(36, 37, 42);
            pnlSidebarHeader.Controls.Add(lblSidebarTitle);
            pnlSidebarHeader.Controls.Add(btnNewGroup);
            pnlSidebarHeader.Controls.Add(btnRefresh);
            pnlSidebarHeader.Dock = DockStyle.Top;
            pnlSidebarHeader.Location = new Point(0, 0);
            pnlSidebarHeader.Name = "pnlSidebarHeader";
            pnlSidebarHeader.Padding = new Padding(12, 0, 4, 0);
            pnlSidebarHeader.Size = new Size(250, 48);
            pnlSidebarHeader.TabIndex = 1;
            // 
            // lblSidebarTitle
            // 
            lblSidebarTitle.Dock = DockStyle.Fill;
            lblSidebarTitle.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            lblSidebarTitle.ForeColor = Color.FromArgb(185, 187, 190);
            lblSidebarTitle.Location = new Point(12, 0);
            lblSidebarTitle.Name = "lblSidebarTitle";
            lblSidebarTitle.Size = new Size(162, 48);
            lblSidebarTitle.TabIndex = 0;
            lblSidebarTitle.Text = "Личные сообщения";
            lblSidebarTitle.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // btnNewGroup
            // 
            btnNewGroup.BackColor = Color.Transparent;
            btnNewGroup.Cursor = Cursors.Hand;
            btnNewGroup.Dock = DockStyle.Right;
            btnNewGroup.FlatAppearance.BorderSize = 0;
            btnNewGroup.FlatStyle = FlatStyle.Flat;
            btnNewGroup.Font = new Font("Segoe UI", 13F);
            btnNewGroup.ForeColor = Color.FromArgb(185, 187, 190);
            btnNewGroup.Location = new Point(174, 0);
            btnNewGroup.Name = "btnNewGroup";
            btnNewGroup.Size = new Size(36, 48);
            btnNewGroup.TabIndex = 1;
            btnNewGroup.Text = "✚";
            btnNewGroup.UseVisualStyleBackColor = false;
            btnNewGroup.Click += btnNewGroup_Click;
            // 
            // btnRefresh
            // 
            btnRefresh.BackColor = Color.Transparent;
            btnRefresh.Cursor = Cursors.Hand;
            btnRefresh.Dock = DockStyle.Right;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.FlatStyle = FlatStyle.Flat;
            btnRefresh.Font = new Font("Segoe UI", 13F);
            btnRefresh.ForeColor = Color.FromArgb(185, 187, 190);
            btnRefresh.Location = new Point(210, 0);
            btnRefresh.Name = "btnRefresh";
            btnRefresh.Size = new Size(36, 48);
            btnRefresh.TabIndex = 2;
            btnRefresh.Text = "↻";
            btnRefresh.UseVisualStyleBackColor = false;
            btnRefresh.Click += btnRefresh_Click;
            // 
            // pnlSidebarFooter
            // 
            pnlSidebarFooter.BackColor = Color.FromArgb(28, 29, 34);
            pnlSidebarFooter.Controls.Add(lblCurrentUser);
            pnlSidebarFooter.Controls.Add(pnlMyAvatar);
            pnlSidebarFooter.Controls.Add(btnSettings);
            pnlSidebarFooter.Dock = DockStyle.Bottom;
            pnlSidebarFooter.Location = new Point(0, 648);
            pnlSidebarFooter.Name = "pnlSidebarFooter";
            pnlSidebarFooter.Padding = new Padding(8, 8, 4, 8);
            pnlSidebarFooter.Size = new Size(250, 52);
            pnlSidebarFooter.TabIndex = 2;
            //
            // pnlMyAvatar — кружок аватарки текущего пользователя (клик/двойной клик — сменить)
            //
            pnlMyAvatar.BackColor = Color.Transparent;
            pnlMyAvatar.Dock = DockStyle.Left;
            pnlMyAvatar.Name = "pnlMyAvatar";
            pnlMyAvatar.Size = new Size(40, 36);
            pnlMyAvatar.Cursor = Cursors.Hand;
            pnlMyAvatar.TabIndex = 0;
            //
            // lblCurrentUser
            //
            lblCurrentUser.Dock = DockStyle.Fill;
            lblCurrentUser.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            lblCurrentUser.ForeColor = Color.FromArgb(220, 221, 222);
            lblCurrentUser.Location = new Point(8, 8);
            lblCurrentUser.Name = "lblCurrentUser";
            lblCurrentUser.Size = new Size(204, 36);
            lblCurrentUser.TabIndex = 1;
            lblCurrentUser.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // btnSettings
            // 
            btnSettings.BackColor = Color.Transparent;
            btnSettings.Cursor = Cursors.Hand;
            btnSettings.Dock = DockStyle.Right;
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.FlatStyle = FlatStyle.Flat;
            btnSettings.Font = new Font("Segoe UI", 14F);
            btnSettings.ForeColor = Color.FromArgb(185, 187, 190);
            btnSettings.Location = new Point(700, 8);
            btnSettings.Name = "btnSettings";
            btnSettings.Size = new Size(34, 36);
            btnSettings.TabIndex = 1;
            btnSettings.Text = "⚙";
            btnSettings.UseVisualStyleBackColor = false;
            btnSettings.Click += btnSettings_Click;
            // 
            // pnlMain
            // 
            pnlMain.BackColor = Color.FromArgb(54, 57, 63);
            pnlMain.Controls.Add(pnlMessages);
            pnlMain.Controls.Add(pnlPreview);
            pnlMain.Controls.Add(pnlInputBar);
            pnlMain.Controls.Add(pnlChatHeader);
            pnlMain.Dock = DockStyle.Fill;
            pnlMain.Location = new Point(250, 0);
            pnlMain.Name = "pnlMain";
            pnlMain.Size = new Size(810, 700);
            pnlMain.TabIndex = 1;
            // 
            // pnlMessages
            // 
            pnlMessages.AutoScroll = true;
            pnlMessages.BackColor = Color.FromArgb(54, 57, 63);
            pnlMessages.Dock = DockStyle.Fill;
            pnlMessages.Location = new Point(0, 48);
            pnlMessages.Name = "pnlMessages";
            pnlMessages.Padding = new Padding(8);
            pnlMessages.Size = new Size(810, 514);
            pnlMessages.TabIndex = 0;
            pnlMessages.Resize += pnlMessages_Resize;
            // 
            // pnlPreview
            // 
            pnlPreview.BackColor = Color.FromArgb(47, 49, 54);
            pnlPreview.Dock = DockStyle.Bottom;
            pnlPreview.Location = new Point(0, 562);
            pnlPreview.Name = "pnlPreview";
            pnlPreview.Size = new Size(810, 70);
            pnlPreview.TabIndex = 1;
            pnlPreview.Visible = false;
            // 
            // pnlInputBar
            // 
            pnlInputBar.BackColor = Color.FromArgb(64, 68, 75);
            pnlInputBar.Controls.Add(btnAttach);
            pnlInputBar.Controls.Add(btnVoice);
            pnlInputBar.Controls.Add(btnVideoCircle);
            pnlInputBar.Controls.Add(txtMessage);
            pnlInputBar.Controls.Add(btnSend);
            pnlInputBar.Dock = DockStyle.Bottom;
            pnlInputBar.Location = new Point(0, 632);
            pnlInputBar.Name = "pnlInputBar";
            pnlInputBar.Padding = new Padding(12, 14, 12, 14);
            pnlInputBar.Size = new Size(810, 68);
            pnlInputBar.TabIndex = 2;
            pnlInputBar.Resize += pnlInputBar_Resize;
            // 
            // btnAttach
            // 
            btnAttach.BackColor = Color.Transparent;
            btnAttach.Cursor = Cursors.Hand;
            btnAttach.FlatAppearance.BorderSize = 0;
            btnAttach.FlatStyle = FlatStyle.Flat;
            btnAttach.Font = new Font("Segoe UI", 16F);
            btnAttach.ForeColor = Color.FromArgb(142, 146, 151);
            btnAttach.Location = new Point(12, 14);
            btnAttach.Name = "btnAttach";
            btnAttach.Size = new Size(40, 40);
            btnAttach.TabIndex = 0;
            btnAttach.Text = "📎";
            btnAttach.UseVisualStyleBackColor = false;
            btnAttach.Click += btnAttach_Click;
            // 
            // btnVoice
            // 
            btnVoice.BackColor = Color.Transparent;
            btnVoice.Cursor = Cursors.Hand;
            btnVoice.FlatAppearance.BorderSize = 0;
            btnVoice.FlatStyle = FlatStyle.Flat;
            btnVoice.Font = new Font("Segoe UI", 16F);
            btnVoice.ForeColor = Color.FromArgb(142, 146, 151);
            btnVoice.Location = new Point(56, 14);
            btnVoice.Name = "btnVoice";
            btnVoice.Size = new Size(40, 40);
            btnVoice.TabIndex = 1;
            btnVoice.Text = "🎤";
            btnVoice.UseVisualStyleBackColor = false;
            btnVoice.MouseDown += btnVoice_MouseDown;
            btnVoice.MouseUp += btnVoice_MouseUp;
            // 
            // btnVideoCircle
            // 
            btnVideoCircle.BackColor = Color.Transparent;
            btnVideoCircle.Cursor = Cursors.Hand;
            btnVideoCircle.FlatAppearance.BorderSize = 0;
            btnVideoCircle.FlatStyle = FlatStyle.Flat;
            btnVideoCircle.Font = new Font("Segoe UI", 16F);
            btnVideoCircle.ForeColor = Color.FromArgb(142, 146, 151);
            btnVideoCircle.Location = new Point(100, 14);
            btnVideoCircle.Name = "btnVideoCircle";
            btnVideoCircle.Size = new Size(40, 40);
            btnVideoCircle.TabIndex = 2;
            btnVideoCircle.Text = "⏺";
            btnVideoCircle.UseVisualStyleBackColor = false;
            btnVideoCircle.Click += btnVideoCircle_Click;
            // 
            // txtMessage
            // 
            txtMessage.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtMessage.BackColor = Color.FromArgb(64, 68, 75);
            txtMessage.BorderStyle = BorderStyle.None;
            txtMessage.Font = new Font("Segoe UI", 10.5F);
            txtMessage.ForeColor = Color.FromArgb(220, 221, 222);
            txtMessage.Location = new Point(146, 6);
            txtMessage.Multiline = true;
            txtMessage.Name = "txtMessage";
            txtMessage.PlaceholderText = "Написать сообщение...";
            txtMessage.Size = new Size(530, 50);
            txtMessage.TabIndex = 3;
            txtMessage.KeyDown += txtMessage_KeyDown;
            txtMessage.PreviewKeyDown += txtMessage_PreviewKeyDown;
            // 
            // btnSend
            // 
            btnSend.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSend.BackColor = Color.FromArgb(88, 101, 242);
            btnSend.Cursor = Cursors.Hand;
            btnSend.FlatAppearance.BorderSize = 0;
            btnSend.FlatStyle = FlatStyle.Flat;
            btnSend.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
            btnSend.ForeColor = Color.White;
            btnSend.Location = new Point(1410, 14);
            btnSend.Name = "btnSend";
            btnSend.Size = new Size(140, 40);
            btnSend.TabIndex = 4;
            btnSend.Text = "➤  Отправить";
            btnSend.UseVisualStyleBackColor = false;
            btnSend.Click += btnSend_Click;
            // 
            // pnlChatHeader
            // 
            pnlChatHeader.BackColor = Color.FromArgb(47, 49, 54);
            pnlChatHeader.Controls.Add(lblChatTitle);
            pnlChatHeader.Dock = DockStyle.Top;
            pnlChatHeader.Location = new Point(0, 0);
            pnlChatHeader.Name = "pnlChatHeader";
            pnlChatHeader.Padding = new Padding(16, 0, 0, 0);
            pnlChatHeader.Size = new Size(810, 48);
            pnlChatHeader.TabIndex = 3;
            pnlChatHeader.Paint += pnlChatHeader_Paint;
            // 
            // lblChatTitle
            // 
            lblChatTitle.Dock = DockStyle.Fill;
            lblChatTitle.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
            lblChatTitle.ForeColor = Color.FromArgb(220, 221, 222);
            lblChatTitle.Location = new Point(16, 0);
            lblChatTitle.Name = "lblChatTitle";
            lblChatTitle.Size = new Size(794, 48);
            lblChatTitle.TabIndex = 0;
            lblChatTitle.Text = "# Выберите диалог";
            lblChatTitle.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // _trayMenu
            // 
            _trayMenu.ImageScalingSize = new Size(20, 20);
            _trayMenu.Name = "_trayMenu";
            _trayMenu.Size = new Size(61, 4);
            // 
            // _trayIcon
            // 
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Text = "PISMO — Мессенджер";
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += TrayMenuOpen_Click;
            // 
            // MainForm
            // 
            BackColor = Color.FromArgb(54, 57, 63);
            ClientSize = new Size(1060, 700);
            Controls.Add(pnlMain);
            Controls.Add(pnlSidebar);
            Font = new Font("Segoe UI", 10.5F);   // крупнее, ближе к Discord
            MinimumSize = new Size(820, 540);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "PISMO — Мессенджер";
            pnlSidebar.ResumeLayout(false);
            pnlSidebarHeader.ResumeLayout(false);
            pnlSidebarFooter.ResumeLayout(false);
            pnlMain.ResumeLayout(false);
            pnlInputBar.ResumeLayout(false);
            pnlInputBar.PerformLayout();
            pnlChatHeader.ResumeLayout(false);
            ResumeLayout(false);
        }
        private TextBox txtMessage;
    }
}