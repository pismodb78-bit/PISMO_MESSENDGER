using System;
using System.Drawing;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using NAudio.Wave;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Окно настроек устройств: выбор камеры и микрофона с тестированием
    /// (превью видео и индикатор уровня громкости).
    /// </summary>
    public class SettingsForm : Form
    {
        // ── Камера ───────────────────────────────────────────────────
        private ComboBox _cmbCamera;
        private Button _btnCameraTest;
        private PictureBox _pbCameraPreview;
        private Label _lblCameraStatus;
        private FilterInfoCollection _videoDevices;
        private VideoCaptureDevice _videoSource;

        // ── Микрофон ─────────────────────────────────────────────────
        private ComboBox _cmbMic;
        private Button _btnMicTest;
        private TrackBar _trkGain;
        private Label _lblGainValue;
        private Panel _pnlLevelBar;
        private Label _lblDbValue;
        private Label _lblMicStatus;
        private WaveInEvent _waveIn;
        private float _currentLevel = 0f;
        private double _currentDb = -100.0;
        private float _gainCached = 1f;
        private System.Windows.Forms.Timer _levelTimer;

        private Button _btnSave;

        // ── Демонстрация экрана ─────────────────────────────────
        private ComboBox _cmbScreenRes;
        private ComboBox _cmbScreenFps;

        // ══ TURN Сервер (для NAT traversal) ═══════════════════════
        private TextBox _txtTurnAddress;
        private NumericUpDown _nudTurnPort;
        private ComboBox _cmbTurnTransport;
        private TextBox _txtTurnUsername;
        private TextBox _txtTurnSecret;
        private NumericUpDown _nudTurnTtl;
        private CheckBox _chkTimeLimited;
        private CheckBox _chkTurnEnabled;
        private Button _btnTestTurn;
        private Label _lblTurnStatus;

        public SettingsForm()
        {
            BuildUi();
            LoadDevices();
            ApplySavedSelection();
            ApplySavedTurnSettings();
        }

        // ════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text = "PISMO — Настройки устройств";
            // Высота 620 влезает на 768px экран (с учётом taskbar ~40px)
            ClientSize = new Size(500, 620);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(54, 57, 63);
            Font = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text = "Настройки устройств",
                Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 16)
            };

            // ── Камера ───────────────────────────────────────────────
            var pnlCamera = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, 60),
                Size = new Size(456, 250)
            };

            var lblCameraTitle = new Label
            {
                Text = "📷 Камера",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(14, 12)
            };

            var lblCameraHint = new Label
            {
                Text = "УСТРОЙСТВО",
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(14, 44)
            };

            _cmbCamera = new ComboBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(14, 62),
                Size = new Size(300, 28)
            };

            _btnCameraTest = new Button
            {
                Text = "▶ Тест",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Location = new Point(326, 61),
                Size = new Size(100, 30),
                Cursor = Cursors.Hand
            };
            _btnCameraTest.FlatAppearance.BorderSize = 0;
            _btnCameraTest.Click += BtnCameraTest_Click;

            _pbCameraPreview = new PictureBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(14, 100),
                Size = new Size(428, 130)
            };

            _lblCameraStatus = new Label
            {
                Text = "Превью не запущено",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize = true,
                Location = new Point(14, 234)
            };

            pnlCamera.Controls.AddRange(new Control[]
            {
                lblCameraTitle, lblCameraHint, _cmbCamera, _btnCameraTest,
                _pbCameraPreview, _lblCameraStatus
            });

            // ── Микрофон ─────────────────────────────────────────────
            var pnlMic = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, 322),
                Size = new Size(456, 220)
            };

            var lblMicTitle = new Label
            {
                Text = "🎤 Микрофон",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(14, 12)
            };

            var lblMicHint = new Label
            {
                Text = "УСТРОЙСТВО",
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(14, 44)
            };

            _cmbMic = new ComboBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(14, 62),
                Size = new Size(300, 28)
            };

            _btnMicTest = new Button
            {
                Text = "▶ Тест",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Location = new Point(326, 61),
                Size = new Size(100, 30),
                Cursor = Cursors.Hand
            };
            _btnMicTest.FlatAppearance.BorderSize = 0;
            _btnMicTest.Click += BtnMicTest_Click;

            var lblGainHint = new Label
            {
                Text = "ЧУВСТВИТЕЛЬНОСТЬ (УСИЛЕНИЕ)",
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(14, 102)
            };

            _trkGain = new TrackBar
            {
                Minimum = 0,
                Maximum = 200,
                Value = 100,
                TickStyle = TickStyle.None,
                Location = new Point(14, 120),
                Size = new Size(300, 30),
                BackColor = Color.FromArgb(47, 49, 54)
            };
            _trkGain.ValueChanged += (s, e) =>
            {
                _lblGainValue.Text = $"{_trkGain.Value}%";
                _gainCached = _trkGain.Value / 100f;
            };

            _lblGainValue = new Label
            {
                Text = "100%",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                AutoSize = true,
                Location = new Point(326, 124)
            };

            var lblLevelHint = new Label
            {
                Text = "УРОВЕНЬ СИГНАЛА",
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(14, 160)
            };

            _lblDbValue = new Label
            {
                Text = "−∞ дБ",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(370, 158),
                TextAlign = ContentAlignment.MiddleRight
            };

            _pnlLevelBar = new Panel
            {
                BackColor = Color.FromArgb(20, 21, 24),
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(14, 180),
                Size = new Size(428, 26)
            };
            _pnlLevelBar.Paint += PnlLevelBar_Paint;

            _lblMicStatus = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize = true,
                Location = new Point(14, 212),
                Visible = false
            };

            pnlMic.Controls.AddRange(new Control[]
            {
                lblMicTitle, lblMicHint, _cmbMic, _btnMicTest,
                lblGainHint, _trkGain, _lblGainValue,
                lblLevelHint, _lblDbValue, _pnlLevelBar, _lblMicStatus
            });

            // ── Демонстрация экрана ─────────────────────────────────
            var pnlScreen = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, 560),
                Size = new Size(456, 100)
            };

            var lblScreenTitle = new Label
            {
                Text = "🖥 Демонстрация экрана",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(14, 6)
            };

            var tblScreen = new TableLayoutPanel
            {
                Location = new Point(14, 36),
                Size = new Size(428, 50),
                ColumnCount = 4,
                RowCount = 1,
                Padding = new Padding(0),
                AutoSize = false,
                BackColor = Color.FromArgb(47, 49, 54)
            };

            // ── Установка ширины колонок (автоматическое распределение) ──
            // Колонка 0: метка "Разрешение..."
            // Колонка 1: ComboBox разрешения
            // Колонка 2: метка "FPS"
            // Колонка 3: ComboBox FPS
            tblScreen.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
            tblScreen.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
            tblScreen.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40f));
            tblScreen.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));

            tblScreen.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));

            // Метка "Разрешение (по высоте, p):"
            var lblRes = new Label
            {
                Text = "Разрешение (по высоте, p):",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(150, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 0)
            };

            _cmbScreenRes = new ComboBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(80, 24),
                Margin = new Padding(0, 0, 0, 0)
            };
            _cmbScreenRes.Items.AddRange(new object[] { "1080", "720", "480", "360" });
            _cmbScreenRes.SelectedIndex = 0;

            // Метка "FPS:"
            var lblFps = new Label
            {
                Text = "FPS:",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(40, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 0)
            };

            _cmbScreenFps = new ComboBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(70, 24),
                Margin = new Padding(0, 0, 0, 0)
            };
            _cmbScreenFps.Items.AddRange(new object[] { "60", "45", "30", "15" });
            _cmbScreenFps.SelectedIndex = 0;

            // Добавляем элементы в таблицу
            tblScreen.Controls.Add(lblRes, 0, 0);
            tblScreen.Controls.Add(_cmbScreenRes, 1, 0);
            tblScreen.Controls.Add(lblFps, 2, 0);
            tblScreen.Controls.Add(_cmbScreenFps, 3, 0);

            pnlScreen.Controls.Add(lblScreenTitle);
            pnlScreen.Controls.Add(tblScreen);

            // ── TURN Server Settings ─────────────────────────────────
            var pnlTurn = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, pnlScreen.Bottom + 14),
                Size = new Size(456, 180)
            };

            var lblTurnTitle = new Label
            {
                Text = "🔗 TURN Сервер (для NAT traversal)",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(14, 12)
            };

            // НОВОЕ: Временные кредитные данные (REST API)
            _chkTimeLimited = new CheckBox
            {
                Text = "Использовать временные кредо (REST API)",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(185, 187, 190),
                BackColor = Color.FromArgb(47, 49, 54),
                Checked = true,
                AutoSize = true,
                Location = new Point(14, 38),
                Cursor = Cursors.Hand
            };
            _chkTimeLimited.CheckedChanged += (s, e) => UpdateTurnFieldsVisibility();
            pnlTurn.Controls.Add(_chkTimeLimited);

            var tblTurn = new TableLayoutPanel
            {
                Location = new Point(14, 60),
                Size = new Size(428, 120),
                ColumnCount = 2,
                RowCount = 6,
                AutoSize = false,
                BackColor = Color.FromArgb(47, 49, 54)
            };

            tblTurn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f));
            tblTurn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292f));
            for (int i = 0; i < 6; i++)
                tblTurn.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));

            // Адрес
            var lblAddr = new Label
            {
                Text = "Адрес:",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _txtTurnAddress = new TextBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f),
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            tblTurn.Controls.Add(lblAddr, 0, 0);
            tblTurn.Controls.Add(_txtTurnAddress, 1, 0);

            // Порт
            var lblPort = new Label
            {
                Text = "Порт:",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _nudTurnPort = new NumericUpDown
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f),
                Minimum = 1,
                Maximum = 65535,
                Value = 3478,
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            tblTurn.Controls.Add(lblPort, 0, 1);
            tblTurn.Controls.Add(_nudTurnPort, 1, 1);

            // Transport
            var lblTransport = new Label
            {
                Text = "Протокол:",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbTurnTransport = new ComboBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            _cmbTurnTransport.Items.AddRange(new object[] { "tcp", "udp", "tls" });
            _cmbTurnTransport.SelectedIndex = 0;
            tblTurn.Controls.Add(lblTransport, 0, 2);
            tblTurn.Controls.Add(_cmbTurnTransport, 1, 2);

            // Username
            var lblUser = new Label
            {
                Text = "Username:",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _txtTurnUsername = new TextBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f),
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            tblTurn.Controls.Add(lblUser, 0, 3);
            tblTurn.Controls.Add(_txtTurnUsername, 1, 3);

            // Secret (для REST API кредо)
            var lblSecret = new Label
            {
                Text = "Secret (hex):",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _txtTurnSecret = new TextBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Consolas", 8f),
                Size = new Size(240, 24),
                Margin = new Padding(0)
            };

            var btnGenerateSecret = new Button
            {
                Text = "🔄",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9f),
                Size = new Size(44, 24),
                Margin = new Padding(0)
            };
            btnGenerateSecret.FlatAppearance.BorderSize = 0;
            // ВОЗВРАЩАЕМ генератор: генерируем username/password, НЕ меняем секрет на сервере.
            btnGenerateSecret.Click += (s, e) => GenerateRandomSecret();

            // Сделаем поле секрет только для чтения — секрет фиксирован на сервере и не должен меняться из клиента.
            _txtTurnSecret.ReadOnly = true;
            btnGenerateSecret.Enabled = true;
            var tt = new ToolTip();
            tt.SetToolTip(btnGenerateSecret, "Генерирует Username и Password для временных кредов. Secret не меняется.");

            var pnlSecret = new Panel
            {
                BackColor = Color.Transparent,
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            _txtTurnSecret.Location = new Point(0, 0);
            btnGenerateSecret.Location = new Point(248, 0);
            pnlSecret.Controls.Add(_txtTurnSecret);
            pnlSecret.Controls.Add(btnGenerateSecret);

            tblTurn.Controls.Add(lblSecret, 0, 4);
            tblTurn.Controls.Add(pnlSecret, 1, 4);

            // TTL (время жизни кредов)
            var lblTtl = new Label
            {
                Text = "TTL (сек):",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = false,
                Size = new Size(120, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _nudTurnTtl = new NumericUpDown
            {
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f),
                Minimum = 60,
                Maximum = 86400,
                Value = 3600,
                Size = new Size(292, 24),
                Margin = new Padding(0)
            };
            tblTurn.Controls.Add(lblTtl, 0, 5);
            tblTurn.Controls.Add(_nudTurnTtl, 1, 5);

            pnlTurn.Controls.Add(lblTurnTitle);
            pnlTurn.Controls.Add(tblTurn);

            _chkTurnEnabled = new CheckBox
            {
                Text = "Включить TURN сервер",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                Checked = true,
                AutoSize = true,
                Location = new Point(14, pnlTurn.Bottom - 20)
            };
            pnlTurn.Controls.Add(_chkTurnEnabled);

            _btnTestTurn = new Button
            {
                Text = "🔍 Проверить",
                BackColor = Color.FromArgb(120, 177, 89),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9f),
                Size = new Size(100, 28),
                Location = new Point(326, pnlTurn.Bottom - 22),
                Cursor = Cursors.Hand
            };
            _btnTestTurn.FlatAppearance.BorderSize = 0;
            _btnTestTurn.Click += BtnTestTurn_Click;
            pnlTurn.Controls.Add(_btnTestTurn);

            _lblTurnStatus = new Label
            {
                Text = "",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize = true,
                Location = new Point(14, pnlTurn.Bottom + 4)
            };
            Controls.Add(_lblTurnStatus);
            Controls.Add(pnlTurn);

            _btnSave = new Button
            {
                Text = "Сохранить и закрыть",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Size = new Size(456, 42),
                Cursor = Cursors.Hand
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += BtnSave_Click;
            _btnSave.MouseEnter += (s, e) => _btnSave.BackColor = Color.FromArgb(71, 82, 196);
            _btnSave.MouseLeave += (s, e) => _btnSave.BackColor = Color.FromArgb(88, 101, 242);

            _btnSave.Location = new Point(20, pnlTurn.Bottom + 20);

            // Вычисляем реальную высоту всего контента
            int contentHeight = _btnSave.Bottom + 20;

            // Создаём прокручиваемую панель — вся высота формы = max 620px
            // (влезает на ноутбук 1366x768 с запасом под taskbar)
            var scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(54, 57, 63),
                Padding = new Padding(0)
            };

            // Переносим все контролы в scrollPanel
            scrollPanel.Controls.AddRange(new Control[]
            {
                lblTitle, pnlCamera, pnlMic, pnlScreen, pnlTurn, _btnSave
            });

            // Форма фиксирована — содержимое скроллится
            this.ClientSize = new Size(500, Math.Min(620, contentHeight));
            Controls.Add(scrollPanel);

            _levelTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _levelTimer.Tick += (s, e) => UpdateLevelBar();
        }

        // ════════════════════════════════════════════════════════════
        //  ЗАГРУЗКА СПИСКОВ УСТРОЙСТВ
        // ════════════════════════════════════════════════════════════
        private void LoadDevices()
        {
            // Камеры (DirectShow)
            try
            {
                _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (_videoDevices.Count == 0)
                {
                    _cmbCamera.Items.Add("Камеры не найдены");
                    _cmbCamera.Enabled = false;
                    _btnCameraTest.Enabled = false;
                }
                else
                {
                    foreach (FilterInfo dev in _videoDevices)
                        _cmbCamera.Items.Add(dev.Name);
                    _cmbCamera.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                _cmbCamera.Items.Add("Ошибка получения списка камер");
                _cmbCamera.Enabled = false;
                _btnCameraTest.Enabled = false;
                _lblCameraStatus.Text = ex.Message;
            }

            // Микрофоны (NAudio / Windows MME)
            try
            {
                int count = WaveInEvent.DeviceCount;
                if (count == 0)
                {
                    _cmbMic.Items.Add("Микрофоны не найдены");
                    _cmbMic.Enabled = false;
                    _btnMicTest.Enabled = false;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        var caps = WaveInEvent.GetCapabilities(i);
                        _cmbMic.Items.Add(caps.ProductName);
                    }
                    _cmbMic.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                _cmbMic.Items.Add("Ошибка получения списка микрофонов");
                _cmbMic.Enabled = false;
                _btnMicTest.Enabled = false;
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = ex.Message;
            }
        }

        /// <summary>Подставляет ранее сохранённые устройства, если они ещё доступны.</summary>
        private void ApplySavedSelection()
        {
            if (!string.IsNullOrWhiteSpace(DeviceSettings.CameraName))
            {
                for (int i = 0; i < _cmbCamera.Items.Count; i++)
                {
                    if (_cmbCamera.Items[i].ToString() == DeviceSettings.CameraName)
                    {
                        _cmbCamera.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (DeviceSettings.MicrophoneIndex >= 0 &&
                DeviceSettings.MicrophoneIndex < _cmbMic.Items.Count)
            {
                _cmbMic.SelectedIndex = DeviceSettings.MicrophoneIndex;
            }
            else if (!string.IsNullOrWhiteSpace(DeviceSettings.MicrophoneName))
            {
                for (int i = 0; i < _cmbMic.Items.Count; i++)
                {
                    if (_cmbMic.Items[i].ToString() == DeviceSettings.MicrophoneName)
                    {
                        _cmbMic.SelectedIndex = i;
                        break;
                    }
                }
            }

            int gainPercent = (int)Math.Round(DeviceSettings.MicrophoneGain * 100);
            _trkGain.Value = Math.Clamp(gainPercent, _trkGain.Minimum, _trkGain.Maximum);
            _lblGainValue.Text = $"{_trkGain.Value}%";
            _gainCached = _trkGain.Value / 100f;

            // ScreenShare
            string sh = DeviceSettings.ScreenShareResolutionHeight.ToString();
            for (int i = 0; i < _cmbScreenRes.Items.Count; i++)
                if (_cmbScreenRes.Items[i].ToString() == sh) { _cmbScreenRes.SelectedIndex = i; break; }
            string sf = DeviceSettings.ScreenShareFps.ToString();
            for (int i = 0; i < _cmbScreenFps.Items.Count; i++)
                if (_cmbScreenFps.Items[i].ToString() == sf) { _cmbScreenFps.SelectedIndex = i; break; }
        }

        // ════════════════════════════════════════════════════════════
        //  ТЕСТ КАМЕРЫ
        // ════════════════════════════════════════════════════════════
        private void BtnCameraTest_Click(object sender, EventArgs e)
        {
            if (_videoSource != null && _videoSource.IsRunning)
            {
                StopCameraPreview();
                _btnCameraTest.Text = "▶ Тест";
                _lblCameraStatus.Text = "Превью остановлено";
                return;
            }

            if (_cmbCamera.SelectedIndex < 0 || _videoDevices == null ||
                _cmbCamera.SelectedIndex >= _videoDevices.Count)
            {
                _lblCameraStatus.Text = "Камера не выбрана";
                return;
            }

            try
            {
                string moniker = _videoDevices[_cmbCamera.SelectedIndex].MonikerString;
                _videoSource = new VideoCaptureDevice(moniker);
                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.Start();

                _btnCameraTest.Text = "■ Стоп";
                _lblCameraStatus.Text = "Запуск камеры…";
            }
            catch (Exception ex)
            {
                _lblCameraStatus.Text = "Ошибка: " + ex.Message;
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                var frame = (Bitmap)eventArgs.Frame.Clone();

                if (_pbCameraPreview.IsHandleCreated)
                {
                    _pbCameraPreview.Invoke(() =>
                    {
                        var old = _pbCameraPreview.Image;
                        _pbCameraPreview.Image = frame;
                        old?.Dispose();
                        _lblCameraStatus.Text = "Превью активно — изображение с камеры";
                    });
                }
                else
                {
                    frame.Dispose();
                }
            }
            catch { }
        }

        private void StopCameraPreview()
        {
            try
            {
                if (_videoSource != null)
                {
                    if (_videoSource.IsRunning)
                    {
                        _videoSource.SignalToStop();
                        _videoSource.WaitForStop();
                    }
                    _videoSource.NewFrame -= VideoSource_NewFrame;
                    _videoSource = null;
                }

                var old = _pbCameraPreview.Image;
                _pbCameraPreview.Image = null;
                old?.Dispose();
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  ТЕСТ МИКРОФОНА (ИНДИКАТОР ГРОМКОСТИ)
        // ════════════════════════════════════════════════════════════
        private void BtnMicTest_Click(object sender, EventArgs e)
        {
            if (_waveIn != null)
            {
                StopMicTest();
                _btnMicTest.Text = "▶ Тест";
                _lblMicStatus.Visible = false;
                return;
            }

            if (_cmbMic.SelectedIndex < 0 || !_cmbMic.Enabled)
            {
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = "Микрофон не выбран";
                return;
            }

            try
            {
                _gainCached = _trkGain.Value / 100f;

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _cmbMic.SelectedIndex,
                    WaveFormat = new WaveFormat(16000, 1)
                };
                _waveIn.DataAvailable += WaveIn_DataAvailable;
                _waveIn.StartRecording();

                _levelTimer.Start();
                _btnMicTest.Text = "■ Стоп";
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = "Говорите — уровень обновляется в реальном времени";
            }
            catch (Exception ex)
            {
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = "Ошибка: " + ex.Message;
            }
        }

        private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            float gain = _gainCached;
            long sum = 0;
            int samples = e.BytesRecorded / 2;
            if (samples == 0) return;

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                int amplified = (int)(sample * gain);
                amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
                sum += (long)amplified * amplified;
            }

            double rms = Math.Sqrt(sum / (double)samples);
            float level = (float)(rms / 32768.0);
            _currentLevel = Math.Clamp(level, 0f, 1f);

            _currentDb = rms > 1 ? 20.0 * Math.Log10(rms / 32768.0) : -100.0;
            _currentDb = Math.Max(_currentDb, -60.0);
        }

        /// <summary>Вызывается таймером в UI-потоке: перерисовывает градусник и обновляет текст дБ.</summary>
        private void UpdateLevelBar()
        {
            _pnlLevelBar.Invalidate();

            _lblDbValue.Text = _currentLevel <= 0.0001f
                ? "−∞ дБ"
                : $"{_currentDb:0.#} дБ";

            _lblDbValue.ForeColor = _currentLevel switch
            {
                > 0.85f => Color.FromArgb(240, 71, 71),
                > 0.6f => Color.FromArgb(250, 166, 26),
                > 0.02f => Color.FromArgb(87, 171, 90),
                _ => Color.FromArgb(114, 118, 125)
            };
        }

        /// <summary>Рисует сегментированный градусник уровня: зелёный → жёлтый → красный на чёрном фоне.</summary>
        private void PnlLevelBar_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int w = _pnlLevelBar.ClientSize.Width;
            int h = _pnlLevelBar.ClientSize.Height;

            const int segCount = 28;
            const int gap = 2;
            float segWidth = (float)(w - gap * (segCount - 1)) / segCount;

            int activeSegments = (int)Math.Round(_currentLevel * segCount);

            for (int i = 0; i < segCount; i++)
            {
                float x = i * (segWidth + gap);

                Color segColor;
                double frac = (double)i / segCount;
                if (frac < 0.6) segColor = Color.FromArgb(87, 171, 90);
                else if (frac < 0.85) segColor = Color.FromArgb(250, 166, 26);
                else segColor = Color.FromArgb(240, 71, 71);

                Brush br = i < activeSegments
                    ? new SolidBrush(segColor)
                    : new SolidBrush(Color.FromArgb(45, 47, 52));

                g.FillRectangle(br, x, 0, segWidth, h);
                br.Dispose();
            }
        }

        private void StopMicTest()
        {
            try
            {
                _levelTimer.Stop();
                _currentLevel = 0f;
                _currentDb = -100.0;
                UpdateLevelBar();

                if (_waveIn != null)
                {
                    _waveIn.StopRecording();
                    _waveIn.DataAvailable -= WaveIn_DataAvailable;
                    _waveIn.Dispose();
                    _waveIn = null;
                }
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  СОХРАНЕНИЕ
        // ════════════════════════════════════════════════════════════
        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (_cmbCamera.Enabled && _cmbCamera.SelectedIndex >= 0)
                DeviceSettings.CameraName = _cmbCamera.Items[_cmbCamera.SelectedIndex].ToString();

            if (_cmbMic.Enabled && _cmbMic.SelectedIndex >= 0)
            {
                DeviceSettings.MicrophoneIndex = _cmbMic.SelectedIndex;
                DeviceSettings.MicrophoneName = _cmbMic.Items[_cmbMic.SelectedIndex].ToString();
            }

            DeviceSettings.MicrophoneGain = _trkGain.Value / 100f;

            if (_cmbScreenRes.SelectedIndex >= 0)
            {
                if (int.TryParse(_cmbScreenRes.SelectedItem.ToString(), out int rh))
                    DeviceSettings.ScreenShareResolutionHeight = rh;
            }
            if (_cmbScreenFps.SelectedIndex >= 0)
            {
                if (int.TryParse(_cmbScreenFps.SelectedItem.ToString(), out int sfps))
                    DeviceSettings.ScreenShareFps = Math.Clamp(sfps, 1, 60);
            }

            TurnSettings.TurnServerAddress = _txtTurnAddress.Text.Trim();
            if (int.TryParse(_nudTurnPort.Value.ToString(), out int port))
                TurnSettings.TurnServerPort = port;
            if (_cmbTurnTransport.SelectedIndex >= 0)
                TurnSettings.TurnTransport = _cmbTurnTransport.SelectedItem.ToString();
            TurnSettings.TurnUsername = _txtTurnUsername.Text.Trim();
            // Не сохраняем секрет из поля, потому что секрет фиксирован на сервере
            // TurnSettings.TurnSecret = _txtTurnSecret.Text.Trim();
            if (int.TryParse(_nudTurnTtl.Value.ToString(), out int ttl))
                TurnSettings.TurnCredentialsTtl = ttl;
            TurnSettings.UseTimeLimitedCredentials = _chkTimeLimited.Checked;
            TurnSettings.TurnEnabled = _chkTurnEnabled.Checked;

            DeviceSettings.Save();

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopCameraPreview();
            StopMicTest();
            _levelTimer?.Stop();
            _levelTimer?.Dispose();
            base.OnFormClosing(e);
        }

        // ════════════════════════════════════════════════════════════
        //  TURN SETTINGS
        // ════════════════════════════════════════════════════════════
        private void ApplySavedTurnSettings()
        {
            _txtTurnAddress.Text = TurnSettings.TurnServerAddress;
            _nudTurnPort.Value = TurnSettings.TurnServerPort;
            _chkTurnEnabled.Checked = TurnSettings.TurnEnabled;
            _chkTimeLimited.Checked = TurnSettings.UseTimeLimitedCredentials;

            int idx = _cmbTurnTransport.Items.IndexOf(TurnSettings.TurnTransport);
            if (idx >= 0) _cmbTurnTransport.SelectedIndex = idx;

            _txtTurnUsername.Text = TurnSettings.TurnUsername;
            // Показываем зафиксированный секрет
            _txtTurnSecret.Text = TurnSettings.TurnSecret;
            _nudTurnTtl.Value = TurnSettings.TurnCredentialsTtl;
        }

        private void UpdateTurnFieldsVisibility()
        {
            bool timeLimited = _chkTimeLimited.Checked;
        }

        private async void BtnTestTurn_Click(object sender, EventArgs e)
        {
            _btnTestTurn.Enabled = false;
            _lblTurnStatus.Text = "Проверка…";
            _lblTurnStatus.ForeColor = Color.FromArgb(185, 187, 190);

            string address = _txtTurnAddress.Text.Trim();
            int port = (int)_nudTurnPort.Value;
            string transport = _cmbTurnTransport.SelectedItem?.ToString() ?? "tcp";

            string username, passwordUtf8 = null;

            if (_chkTimeLimited.Checked)
            {
                string secret = _txtTurnSecret.Text.Trim();
                int ttl = (int)_nudTurnTtl.Value;
                string user = string.IsNullOrWhiteSpace(_txtTurnUsername.Text.Trim())
                                ? "testuser"
                                : _txtTurnUsername.Text.Trim();

                long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl;
                username = $"{expiry}:{user}";

                // coturn всегда использует static-auth-secret как UTF8-текст, не hex.
                // Единственно верный вариант — без угадывания формата секрета.
                using (var hmacUtf8 = new System.Security.Cryptography.HMACSHA1(
                           System.Text.Encoding.UTF8.GetBytes(secret)))
                {
                    byte[] hashUtf8 = hmacUtf8.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username));
                    passwordUtf8 = Convert.ToBase64String(hashUtf8);
                }
            }
            else
            {
                username = _txtTurnUsername.Text.Trim();
                passwordUtf8 = _txtTurnSecret.Text.Trim();
            }

            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var cts = new System.Threading.CancellationTokenSource(5000);
                await tcp.ConnectAsync(address, port, cts.Token);

                _lblTurnStatus.Text = $"✅ Соединение установлено ({address}:{port}/{transport}). Username: {username}";
                _lblTurnStatus.ForeColor = Color.FromArgb(87, 171, 90);
            }
            catch (OperationCanceledException)
            {
                _lblTurnStatus.Text = "❌ Таймаут — сервер не отвечает";
                _lblTurnStatus.ForeColor = Color.FromArgb(240, 71, 71);
            }
            catch (Exception ex)
            {
                // При ошибке показываем вычисленный пароль (без показа самого секрета)
                var info = $"Ошибка: {ex.Message}";
                if (!string.IsNullOrEmpty(passwordUtf8))
                    info += $"\npwd = {passwordUtf8}";

                _lblTurnStatus.Text = "❌ Ошибка при проверке — см. детали";
                _lblTurnStatus.ForeColor = Color.FromArgb(240, 71, 71);

                System.Diagnostics.Debug.WriteLine("[TURN TEST] " + info);
                MessageBox.Show(info, "TURN test debug", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _btnTestTurn.Enabled = true;
            }
        }

        /// <summary>Генерирует TURN Username и Password (не меняет Secret).</summary>
        private void GenerateRandomSecret()
        {
            // Генерация username/password (не меняем сам Secret).
            // Используем фиксированный baseUser = "alice", чтобы менялась только числовая часть.
            string secret = _txtTurnSecret.Text.Trim();
            if (string.IsNullOrWhiteSpace(secret))
            {
                MessageBox.Show(
                    "Секрет TURN не задан в конфиге/на сервере. Невозможно вычислить пароль для временных кредов.\n" +
                    "Чтобы протестировать полностью — установите Secret в конфиге сервера или в TurnSettings.",
                    "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                int ttl = (int)_nudTurnTtl.Value;
                // Фиксированный базовый пользователь — у всех будет одинаково, уникальность даёт expiry
                string baseUser = "alice";

                long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl;
                string usernameWithExpiry = $"{expiry}:{baseUser}";

                string password;
                // ВАЖНО: coturn интерпретирует static-auth-secret как ОБЫЧНЫЙ ТЕКСТ
                // (UTF8-байты строки), а не как hex-encoded байты — даже если сам текст
                // секрета выглядит как валидная hex-строка. Раньше здесь было сначала
                // "угадывание" через Convert.FromHexString(secret): для секретов вида
                // "79d2240e..." (все символы 0-9a-f, чётная длина) это ВСЕГДА успешно
                // декодировалось как hex и использовался неправильный 20-байтовый ключ
                // вместо правильного UTF8-ключа из исходной строки — отсюда и неверный
                // password, который не совпадал с тем, что реально проверяет coturn.
                using (var hmac = new System.Security.Cryptography.HMACSHA1(
                           System.Text.Encoding.UTF8.GetBytes(secret)))
                {
                    byte[] hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(usernameWithExpiry));
                    password = Convert.ToBase64String(hash);
                }

                // Показываем базовый user в поле (поле Username в UI — базовый логин)
                _txtTurnUsername.Text = baseUser;

                _lblTurnStatus.Text = $"✅ Сгенерировано: {baseUser}";
                _lblTurnStatus.ForeColor = Color.FromArgb(87, 171, 90);
                _lblTurnStatus.Visible = true;

                string psCommand = $"$secret='{secret}';$ttl={_nudTurnTtl.Value};$user='{baseUser}';$expiry=[int]([System.DateTime]::UtcNow.Subtract([datetime]'1970-01-01').TotalSeconds)+$ttl;$username=\"$expiry`:{baseUser}\";$h=New-Object System.Security.Cryptography.HMACSHA1;$h.Key=[System.Text.Encoding]::UTF8.GetBytes($secret);$pwd=$h.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($username));$password=[Convert]::ToBase64String($pwd);Write-Output \"CREDS: $username $password\"";

                System.Windows.Forms.Clipboard.SetText(psCommand);

                ShowCredentialsDialog(usernameWithExpiry, password, secret, baseUser, psCommand);
            }
            catch (Exception ex)
            {
                _lblTurnStatus.Text = $"❌ Ошибка генерации: {ex.Message}";
                _lblTurnStatus.ForeColor = Color.FromArgb(240, 71, 71);
                _lblTurnStatus.Visible = true;
            }
        }

        private void ShowCredentialsDialog(string username, string password, string hexSecret,
            string baseUser, string psCommand)
        {
            var credForm = new Form
            {
                Text = "TURN Credentials",
                Width = 600,
                Height = 400,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(54, 57, 63),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f)
            };

            var lblTitle = new Label
            {
                Text = "✅ Временные кредо сгенерированы",
                Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(87, 171, 90),
                Location = new Point(20, 20),
                AutoSize = true
            };

            var lblCreds = new Label
            {
                Text = $"CREDS: {username} {password}",
                Font = new Font("Consolas", 9f),
                ForeColor = Color.FromArgb(220, 221, 222),
                Location = new Point(20, 60),
                Size = new Size(540, 60),
                AutoSize = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(32, 34, 37)
            };

            var lblExpiry = new Label
            {
                Text = $"Истекает: {DateTimeOffset.FromUnixTimeSeconds(long.Parse(username.Split(':')[0])):O}",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize = true,
                Location = new Point(20, 130),

            };

            var lblNote = new Label
            {
                Text = "Каждый клиент может генерировать новые кредо используя Secret (hex):\n" +
                       "Ключ: " + (hexSecret.Length > 32 ? hexSecret.Substring(0, 32) + "..." : hexSecret),
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                Location = new Point(20, 160),
                Size = new Size(540, 80),
                AutoSize = false
            };

            var btnCopy = new Button
            {
                Text = "📋 Скопировать кредо",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(20, 250),
                Size = new Size(150, 35),
                Cursor = Cursors.Hand
            };
            btnCopy.FlatAppearance.BorderSize = 0;
            btnCopy.Click += (s, e) =>
            {
                System.Windows.Forms.Clipboard.SetText($"CREDS: {username} {password}");
                MessageBox.Show("✅ Кредо скопированы в буфер обмена!", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            var btnSaveDb = new Button
            {
                Text = "💾 Сохранить в БД",
                BackColor = Color.FromArgb(120, 177, 89),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(180, 250),
                Size = new Size(150, 35),
                Cursor = Cursors.Hand
            };
            btnSaveDb.FlatAppearance.BorderSize = 0;
            btnSaveDb.Click += (s, e) =>
            {
                SaveTurnToDatabase(hexSecret, baseUser);
                SaveTurnToFile(hexSecret, baseUser);
                MessageBox.Show("✅ Ключи сохранены в БД и файл (turn.key)!", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                credForm.Close();
            };

            var btnPowerShell = new Button
            {
                Text = "⚡ PowerShell",
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(340, 250),
                Size = new Size(120, 35),
                Cursor = Cursors.Hand
            };
            btnPowerShell.FlatAppearance.BorderSize = 0;
            btnPowerShell.Click += (s, e) => RunPowerShellCommand(psCommand);

            var btnClose = new Button
            {
                Text = "✕ Закрыть",
                BackColor = Color.FromArgb(100, 100, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(470, 250),
                Size = new Size(90, 35),
                Cursor = Cursors.Hand,
                DialogResult = DialogResult.OK
            };
            btnClose.FlatAppearance.BorderSize = 0;

            credForm.Controls.Add(lblTitle);
            credForm.Controls.Add(lblCreds);
            credForm.Controls.Add(lblExpiry);
            credForm.Controls.Add(lblNote);
            credForm.Controls.Add(btnCopy);
            credForm.Controls.Add(btnSaveDb);
            credForm.Controls.Add(btnPowerShell);
            credForm.Controls.Add(btnClose);

            credForm.ShowDialog(this);
        }

        private void SaveTurnToDatabase(string hexSecret, string username)
        {
            try
            {
                TurnServerRepository.SaveTurnServer(
                    "default",
                    _txtTurnAddress.Text.Trim(),
                    (int)_nudTurnPort.Value,
                    _cmbTurnTransport.SelectedItem?.ToString() ?? "tcp",
                    username,
                    hexSecret,
                    (int)_nudTurnTtl.Value,
                    _chkTimeLimited.Checked);

                System.Diagnostics.Debug.WriteLine($"[TURN] Ключ сохранён в БД: {username}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка сохранения в БД: {ex.Message}");
            }
        }

        private void SaveTurnToFile(string hexSecret, string username)
        {
            try
            {
                string filePath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "turn.key");

                string content = $"# PISMO TURN Server Credentials\n" +
                    $"# Сгенерировано: {DateTime.Now:O}\n" +
                    $"# Данный файл содержит конфиденциальный SECRET ключ!\n" +
                    $"# ХРАНИ В БЕЗОПАСНОСТИ!\n\n" +
                    $"ServerAddress={_txtTurnAddress.Text.Trim()}\n" +
                    $"ServerPort={_nudTurnPort.Value}\n" +
                    $"Transport={_cmbTurnTransport.SelectedItem?.ToString() ?? "tcp"}\n" +
                    $"Username={username}\n" +
                    $"SecretHex={hexSecret}\n" +
                    $"TtlSeconds={_nudTurnTtl.Value}\n" +
                    $"UseTimeLimited=true\n" +
                    $"CreatedAt={DateTime.UtcNow:O}\n\n" +
                    $"# Команда для генерации текущего пароля (PowerShell):\n" +
                    $"# $secret='{hexSecret}';$ttl={_nudTurnTtl.Value};$user='{username}';$expiry=[int]([System.DateTime]::UtcNow.Subtract([datetime]'1970-01-01').TotalSeconds)+$ttl;$username=\"$expiry`:{username}\";$h=New-Object System.Security.Cryptography.HMACSHA1;$h.Key=[System.Text.Encoding]::UTF8.GetBytes($secret);$pwd=$h.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($username));$password=[Convert]::ToBase64String($pwd);Write-Output \"CREDS: $username $password\"\n";

                System.IO.File.WriteAllText(filePath, content);
                System.Diagnostics.Debug.WriteLine($"[TURN] Ключ сохранён в файл: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка сохранения в файл: {ex.Message}");
            }
        }

        private void RunPowerShellCommand(string command)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -Command \"{command}\"",
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Ошибка запуска PowerShell: {ex.Message}", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

}