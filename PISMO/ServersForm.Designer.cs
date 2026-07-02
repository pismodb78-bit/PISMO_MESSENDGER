using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using NAudio.Wave;

namespace PISMO
{
    // ДИЗАЙН формы (построение интерфейса). Вынесен из логики по образцу
    // MainForm.Designer.cs: здесь ТОЛЬКО создание/раскладка контролов.
    public sealed partial class ServersForm
    {
        private void BuildUi()
        {
            _pnlServers = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 34, 37), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            _pnlChannels = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(47, 49, 54), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
            _pnlMembers = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 180, BackColor = Color.FromArgb(47, 49, 54), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };

            var center = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(54, 57, 63) };
            _lblTitle = new Label { Dock = DockStyle.Top, Height = 36, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Text = "Выберите канал" };
            _pnlMessages = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(54, 57, 63), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };

            _pnlInput = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(64, 68, 75), Visible = false };
            _txtInput = new TextBox { Dock = DockStyle.Fill, Multiline = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, Font = new Font("Segoe UI", 11f) };
            _txtInput.KeyDown += TxtInput_KeyDown;
            _txtInput.TextChanged += (s, e) => UpdateMentionPopup();
            _txtInput.LostFocus += (s, e) => { if (_mentionPopup != null && !_mentionPopup.Focused) HideMentionPopup(); };
            var btnSend = new Button { Dock = DockStyle.Right, Width = 90, Text = "Отправить", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White };
            btnSend.FlatAppearance.BorderSize = 0;
            btnSend.Click += (s, e) => SendChannelMessage();
            var btnGif = new Button { Dock = DockStyle.Right, Width = 52, Text = "GIF", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            btnGif.FlatAppearance.BorderSize = 0;
            btnGif.Click += (s, e) => OpenServerGifPicker();

            // Те же инструменты, что и в обычном чате: прикрепить файл, картинку,
            // записать голосовое (зажать), записать видео-кружок.
            Button ToolBtn(string txt) => new Button { Dock = DockStyle.Right, Width = 40, Text = txt, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.FromArgb(220, 221, 222), Font = new Font("Segoe UI", 12f), Cursor = Cursors.Hand };
            var btnAttach = ToolBtn("📎"); btnAttach.FlatAppearance.BorderSize = 0; btnAttach.Click += (s, e) => AttachChannelFile(false);
            var btnImage = ToolBtn("🖼"); btnImage.FlatAppearance.BorderSize = 0; btnImage.Click += (s, e) => AttachChannelFile(true);
            _btnChVoice = ToolBtn("🎤"); _btnChVoice.FlatAppearance.BorderSize = 0;
            _btnChVoice.MouseDown += ChVoice_MouseDown; _btnChVoice.MouseUp += ChVoice_MouseUp;
            var btnCircle = ToolBtn("⭕"); btnCircle.FlatAppearance.BorderSize = 0; btnCircle.Click += (s, e) => RecordChannelCircle();

            var inputHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 10) };
            inputHost.Controls.Add(_txtInput);
            _pnlInput.Controls.Add(inputHost);
            // Порядок (Dock=Right) справа налево: Отправить, GIF, кружок, голос, картинка, файл.
            _pnlInput.Controls.Add(btnAttach);
            _pnlInput.Controls.Add(btnImage);
            _pnlInput.Controls.Add(_btnChVoice);
            _pnlInput.Controls.Add(btnCircle);
            _pnlInput.Controls.Add(btnGif);
            _pnlInput.Controls.Add(btnSend);

            // Полоска «Ответ на …» над полем ввода.
            _replyBar = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = Color.FromArgb(54, 57, 63), Visible = false };
            _lblReply = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(0, 176, 244), Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
            var btnReplyCancel = new Button { Dock = DockStyle.Right, Width = 36, Text = "✕", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(54, 57, 63), ForeColor = Color.White, Cursor = Cursors.Hand };
            btnReplyCancel.FlatAppearance.BorderSize = 0;
            btnReplyCancel.Click += (s, e) => CancelServerReply();
            _replyBar.Controls.Add(_lblReply);
            _replyBar.Controls.Add(btnReplyCancel);

            // Низ центра: контейнер с полоской ответа над полем ввода.
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            _pnlInput.Dock = DockStyle.Fill;
            bottom.Controls.Add(_pnlInput);
            bottom.Controls.Add(_replyBar);
            _bottomDock = bottom;

            center.Controls.Add(_pnlMessages);
            center.Controls.Add(bottom);
            center.Controls.Add(_lblTitle);

            // Поиск серверов (над списком серверов).
            _serverSearch = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, Font = new Font("Segoe UI", 9f), PlaceholderText = "Поиск серверов…", Margin = new Padding(0) };
            _serverSearch.TextChanged += (s, e) => FilterServers(_serverSearch.Text);
            var serverHost = new Panel { Dock = DockStyle.Left, Width = 180, BackColor = Color.FromArgb(32, 34, 37) };
            serverHost.Controls.Add(_pnlServers);
            serverHost.Controls.Add(_serverSearch);

            // Поиск каналов + кнопка «Обновить» (над списком каналов).
            _channelSearch = new TextBox { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White, Font = new Font("Segoe UI", 9f), PlaceholderText = "Поиск каналов…" };
            _channelSearch.TextChanged += (s, e) => FilterChannels(_channelSearch.Text);
            var btnRefreshSrv = new Button { Dock = DockStyle.Top, Height = 28, Text = "↻ Обновить", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(64, 68, 75), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), Cursor = Cursors.Hand };
            btnRefreshSrv.FlatAppearance.BorderSize = 0;
            btnRefreshSrv.Click += (s, e) => RefreshNow();
            var channelHost = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Color.FromArgb(47, 49, 54) };
            channelHost.Controls.Add(_pnlChannels);
            channelHost.Controls.Add(_channelSearch);
            channelHost.Controls.Add(btnRefreshSrv);

            Controls.Add(center);
            Controls.Add(_pnlMembers);
            Controls.Add(channelHost);
            Controls.Add(serverHost);
            _serverHostCol = serverHost;
        }
    }
}
