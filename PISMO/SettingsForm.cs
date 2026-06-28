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
        private CheckBox _chkVoiceAuto;
        private CheckBox _chkNoiseSuppress;
        private TrackBar _trkVoiceThreshold;
        private Label _lblVoiceThresholdValue;
        private Panel _pnlLevelBar;
        private Label _lblDbValue;
        private Label _lblMicStatus;
        private WaveInEvent _waveIn;
        private WaveOutEvent _monitorOut;          // воспроизведение «слышу себя» при тесте
        private BufferedWaveProvider _monitorBuf;
        private float _currentLevel = 0f;
        private double _currentDb = -100.0;
        private float _gainCached = 1f;
        private System.Windows.Forms.Timer _levelTimer;

        private Button _btnSave;

        // ── Демонстрация экрана ─────────────────────────────────
        private ComboBox _cmbScreenRes;
        private ComboBox _cmbScreenFps;

        public SettingsForm()
        {
            BuildUi();
            LoadDevices();
            ApplySavedSelection();
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
                Size = new Size(456, 348)
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

            // ── Активация голоса (порог регистрации) ────────────────────
            _chkVoiceAuto = new CheckBox
            {
                Text = "Автоматически определять чувствительность ввода",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                AutoSize = true,
                Location = new Point(14, 236),
                Cursor = Cursors.Hand
            };
            var lblVoiceHint = new Label
            {
                Text = "Ниже порога звук с микрофона не передаётся (как в Discord).",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(150, 152, 158),
                AutoSize = true,
                Location = new Point(14, 258)
            };
            _trkVoiceThreshold = new TrackBar
            {
                Minimum = -60,   // дБ: тихо
                Maximum = 0,     // дБ: громко
                Value = -40,
                TickStyle = TickStyle.None,
                Location = new Point(14, 276),
                Size = new Size(300, 30),
                BackColor = Color.FromArgb(47, 49, 54)
            };
            _lblVoiceThresholdValue = new Label
            {
                Text = "−40 дБ",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                AutoSize = true,
                Location = new Point(326, 280)
            };
            _trkVoiceThreshold.ValueChanged += (s, e) =>
            {
                _lblVoiceThresholdValue.Text = $"{_trkVoiceThreshold.Value} дБ";
                _pnlLevelBar.Invalidate(); // переносим метку порога на градуснике
            };
            _chkVoiceAuto.CheckedChanged += (s, e) =>
            {
                // В авто-режиме ручной порог не нужен — гасим слайдер.
                _trkVoiceThreshold.Enabled = !_chkVoiceAuto.Checked;
                _lblVoiceThresholdValue.Enabled = !_chkVoiceAuto.Checked;
            };

            _chkNoiseSuppress = new CheckBox
            {
                Text = "Шумоподавление (RNNoise: давит клавиатуру/мышь/шум)",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                AutoSize = true,
                Location = new Point(14, 314),
                Cursor = Cursors.Hand
            };

            pnlMic.Controls.AddRange(new Control[]
            {
                _chkNoiseSuppress,
                lblMicTitle, lblMicHint, _cmbMic, _btnMicTest,
                lblGainHint, _trkGain, _lblGainValue,
                lblLevelHint, _lblDbValue, _pnlLevelBar, _lblMicStatus,
                _chkVoiceAuto, lblVoiceHint, _trkVoiceThreshold, _lblVoiceThresholdValue
            });

            // ── Демонстрация экрана ─────────────────────────────────
            var pnlScreen = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, 690),
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

            // ── Горячие клавиши в звонке ────────────────────────────────
            var pnlKeys = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, pnlScreen.Bottom + 14),
                Size = new Size(456, 150)
            };
            pnlKeys.Controls.Add(new Label
            {
                Text = "⌨ Горячие клавиши (в звонке)",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(14, 8)
            });

            Button MakeKeyRow(string caption, int y, Func<int> get, Action<int> set)
            {
                pnlKeys.Controls.Add(new Label
                {
                    Text = caption,
                    Font = new Font("Segoe UI", 9.5f),
                    ForeColor = Color.FromArgb(200, 201, 203),
                    AutoSize = false,
                    Size = new Size(200, 26),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(14, y)
                });
                var btn = new Button
                {
                    Text = HotkeyToText(get()),
                    BackColor = Color.FromArgb(32, 34, 37),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                    Size = new Size(210, 28),
                    Location = new Point(220, y - 1),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Color.FromArgb(80, 82, 88);
                bool capturing = false;
                btn.Click += (s, e) => { capturing = true; btn.Text = "Нажмите клавиши…"; btn.Focus(); };
                btn.KeyDown += (s, e) =>
                {
                    if (!capturing) return;
                    e.SuppressKeyPress = true;
                    if (e.KeyCode == Keys.Escape) { capturing = false; btn.Text = HotkeyToText(get()); return; }
                    if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete) { set(0); capturing = false; btn.Text = "—"; return; }
                    // Игнорируем «голые» модификаторы — ждём основную клавишу.
                    if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return;
                    int combo = (int)(e.KeyCode | (e.Control ? Keys.Control : 0) | (e.Alt ? Keys.Alt : 0) | (e.Shift ? Keys.Shift : 0));
                    set(combo);
                    capturing = false;
                    btn.Text = HotkeyToText(combo);
                };
                pnlKeys.Controls.Add(btn);
                return btn;
            }

            MakeKeyRow("Микрофон (вкл/выкл):", 40, () => DeviceSettings.HotkeyMic, v => DeviceSettings.HotkeyMic = v);
            MakeKeyRow("Камера (вкл/выкл):", 74, () => DeviceSettings.HotkeyCamera, v => DeviceSettings.HotkeyCamera = v);
            MakeKeyRow("Демонстрация (вкл/выкл):", 108, () => DeviceSettings.HotkeyScreen, v => DeviceSettings.HotkeyScreen = v);

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

            _btnSave.Location = new Point(20, pnlKeys.Bottom + 20);

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
                lblTitle, pnlCamera, pnlMic, pnlScreen, pnlKeys, _btnSave
            });

            // Форма фиксирована — содержимое скроллится
            this.ClientSize = new Size(500, Math.Min(620, contentHeight));
            Controls.Add(scrollPanel);

            _levelTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _levelTimer.Tick += (s, e) => UpdateLevelBar();
        }

        /// <summary>Человекочитаемое представление комбинации клавиш ((int)Keys).</summary>
        private static string HotkeyToText(int value)
        {
            if (value == 0) return "—";
            var k = (Keys)value;
            var parts = new System.Collections.Generic.List<string>();
            if ((k & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((k & Keys.Alt) == Keys.Alt) parts.Add("Alt");
            if ((k & Keys.Shift) == Keys.Shift) parts.Add("Shift");
            parts.Add((k & Keys.KeyCode).ToString());
            return string.Join(" + ", parts);
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

            // Шумоподавление.
            _chkNoiseSuppress.Checked = DeviceSettings.NoiseSuppression;

            // Активация голоса (порог в дБ).
            _chkVoiceAuto.Checked = DeviceSettings.VoiceAutoSensitivity;
            _trkVoiceThreshold.Value = Math.Clamp(DeviceSettings.VoiceThreshold, -60, 0);
            _lblVoiceThresholdValue.Text = $"{_trkVoiceThreshold.Value} дБ";
            _trkVoiceThreshold.Enabled = !_chkVoiceAuto.Checked;
            _lblVoiceThresholdValue.Enabled = !_chkVoiceAuto.Checked;

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

                var fmt = new WaveFormat(16000, 1);
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _cmbMic.SelectedIndex,
                    WaveFormat = fmt
                };
                _waveIn.DataAvailable += WaveIn_DataAvailable;

                // «Слышу себя»: проигрываем микрофон обратно с учётом порога —
                // чтобы вживую подобрать чувствительность.
                try
                {
                    _monitorBuf = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
                    _monitorOut = new WaveOutEvent { DesiredLatency = 120 };
                    _monitorOut.Init(_monitorBuf);
                    _monitorOut.Play();
                }
                catch { _monitorOut = null; _monitorBuf = null; }

                _waveIn.StartRecording();

                _levelTimer.Start();
                _btnMicTest.Text = "■ Стоп";
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = _chkVoiceAuto.Checked
                    ? "Говорите — вы слышите себя; порог в авто-режиме"
                    : "Говорите — вы слышите себя; двигайте порог, чтобы отсечь тихое";
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

            // Воспроизводим обратно (с усилением), применяя порог как в реальном
            // звонке: тише порога — тишина, поэтому слышно, что именно отсекается.
            if (_monitorBuf != null)
            {
                bool open = _chkVoiceAuto.Checked || _currentDb >= _trkVoiceThreshold.Value;
                var outBuf = new byte[e.BytesRecorded];
                if (open)
                {
                    for (int i = 0; i < e.BytesRecorded; i += 2)
                    {
                        short s = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                        int a = Math.Clamp((int)(s * gain), short.MinValue, short.MaxValue);
                        outBuf[i] = (byte)(a & 0xFF);
                        outBuf[i + 1] = (byte)((a >> 8) & 0xFF);
                    }
                }
                try { _monitorBuf.AddSamples(outBuf, 0, outBuf.Length); } catch { }
            }
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

            // Заполнение по дБ-шкале (−60..0 дБ → 0..1), чтобы совпадать с меткой порога.
            double lvlNorm = _currentLevel <= 0.0001f ? 0 : Math.Clamp((_currentDb + 60) / 60.0, 0, 1);
            int activeSegments = (int)Math.Round(lvlNorm * segCount);

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

            // Метка порога активации (только в ручном режиме): вертикальная линия.
            if (_trkVoiceThreshold != null && !_chkVoiceAuto.Checked)
            {
                // Порог в дБ (−60..0) → доля шкалы 0..1 (как уровень: ~ -60дБ→0, 0дБ→1).
                double norm = Math.Clamp((_trkVoiceThreshold.Value + 60) / 60.0, 0, 1);
                int mx = (int)(norm * w);
                using var pen = new Pen(Color.White, 2);
                g.DrawLine(pen, mx, 0, mx, h);
                using var tri = new SolidBrush(Color.White);
                g.FillPolygon(tri, new[] { new Point(mx - 4, 0), new Point(mx + 4, 0), new Point(mx, 5) });
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

                if (_monitorOut != null)
                {
                    try { _monitorOut.Stop(); _monitorOut.Dispose(); } catch { }
                    _monitorOut = null;
                }
                _monitorBuf = null;
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

            DeviceSettings.VoiceAutoSensitivity = _chkVoiceAuto.Checked;
            DeviceSettings.VoiceThreshold = _trkVoiceThreshold.Value;
            DeviceSettings.NoiseSuppression = _chkNoiseSuppress.Checked;

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

            // TURN-сервер больше не используется (звонки через LiveKit).

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

    }

}