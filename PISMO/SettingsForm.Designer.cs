using System;
using System.Drawing;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using NAudio.Wave;
using System.IO;

namespace PISMO
{
    // ДИЗАЙН формы (построение интерфейса). Вынесен из логики по образцу
    // MainForm.Designer.cs: здесь ТОЛЬКО создание/раскладка контролов.
    public partial class SettingsForm
    {
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
                Size = new Size(456, 252)
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
                // Применяем громкость СРАЗУ: и к открытому тесту микрофона, и в
                // настройки — активный звонок подхватит её на лету (таймер CallForm),
                // без нажатия «Сохранить».
                DeviceSettings.MicrophoneGain = _gainCached;
                if (_micTest != null && !_micTest.IsDisposed) _micTest.ApplyGain(_gainCached);
            };

            _lblGainValue = new Label
            {
                Text = "100%",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                AutoSize = true,
                Location = new Point(326, 124)
            };

            // Старый градусник уровня (от NAudio-теста) убран — проверка теперь
            // в отдельном окне «Тест» с реальным шумодавом. Контролы оставляем
            // невидимыми, чтобы не трогать остальную раскладку/код.
            var lblLevelHint = new Label { Visible = false };
            _lblDbValue = new Label { Visible = false };
            _pnlLevelBar = new Panel { Visible = false, Size = new Size(0, 0) };

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
                Text = "Ниже порога звук с микрофона не передаётся.",
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

            // Самописный порог активации убран (ломал mute). Шум давит Krisp —
            // прячем эти контролы, чтобы не путать.
            _chkVoiceAuto.Visible = false;
            lblVoiceHint.Visible = false;
            _trkVoiceThreshold.Visible = false;
            _lblVoiceThresholdValue.Visible = false;

            _chkNoiseSuppress = new CheckBox
            {
                Text = "Шумоподавление",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                AutoSize = true,
                Location = new Point(14, 162),
                Cursor = Cursors.Hand
            };

            // Ползунок СИЛЫ шумодава (0..100 %). Регулирует wet/dry-микс денойзера и
            // применяется на лету — в т.ч. к идущему звонку.
            var lblNoiseHint = new Label
            {
                Text = "Сила шумоподавления",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(140, 142, 146),
                AutoSize = true,
                Location = new Point(14, 186)
            };
            _trkNoiseStrength = new TrackBar
            {
                Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10,
                Location = new Point(12, 204), Size = new Size(260, 40),
                BackColor = Color.FromArgb(47, 49, 54)
            };
            _lblNoiseStrengthValue = new Label
            {
                Text = "100%", Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222), AutoSize = true, Location = new Point(280, 212)
            };

            // Живо применяем смену устройства/шумодава к открытому тесту микрофона.
            _cmbMic.SelectedIndexChanged += (s, e) =>
            {
                if (_micTest != null && !_micTest.IsDisposed)
                    _micTest.ApplyConfig(_chkNoiseSuppress.Checked, _cmbMic.SelectedItem?.ToString());
            };
            _chkNoiseSuppress.CheckedChanged += (s, e) =>
            {
                // Чекбокс = быстрый вкл/выкл: тянет ползунок в 0 или 100.
                if (_chkNoiseSuppress.Checked && _trkNoiseStrength.Value == 0) _trkNoiseStrength.Value = 100;
                else if (!_chkNoiseSuppress.Checked && _trkNoiseStrength.Value > 0) _trkNoiseStrength.Value = 0;
                if (_micTest != null && !_micTest.IsDisposed)
                    _micTest.ApplyConfig(_chkNoiseSuppress.Checked, _cmbMic.SelectedItem?.ToString());
            };
            _trkNoiseStrength.ValueChanged += (s, e) =>
            {
                int v = _trkNoiseStrength.Value;
                _lblNoiseStrengthValue.Text = v + "%";
                // Синхронизируем чекбокс без рекурсии.
                if ((v > 0) != _chkNoiseSuppress.Checked) _chkNoiseSuppress.Checked = v > 0;
                // Применяем ЖИВО: тест микрофона + идущий звонок.
                DeviceSettings.NoiseSuppressionStrength = v;
                if (_micTest != null && !_micTest.IsDisposed)
                    _micTest.ApplyConfig(v > 0, _cmbMic.SelectedItem?.ToString());
                try { MainForm.Current?.ActiveCallFormPublic()?.SetNoiseStrengthLive(v); } catch { }
            };

            pnlMic.Controls.AddRange(new Control[]
            {
                _chkNoiseSuppress, lblNoiseHint, _trkNoiseStrength, _lblNoiseStrengthValue,
                lblMicTitle, lblMicHint, _cmbMic, _btnMicTest,
                lblGainHint, _trkGain, _lblGainValue,
                lblLevelHint, _lblDbValue, _pnlLevelBar, _lblMicStatus,
                _chkVoiceAuto, lblVoiceHint, _trkVoiceThreshold, _lblVoiceThresholdValue
            });

            // ── Демонстрация экрана ─────────────────────────────────
            var pnlScreen = new Panel
            {
                BackColor = Color.FromArgb(47, 49, 54),
                Location = new Point(20, 538),
                Size = new Size(456, 186)
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
                Size = new Size(428, 82),
                ColumnCount = 4,
                RowCount = 2,
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
            tblScreen.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

            // Метка "Разрешение (по высоте, p):"
            var lblRes = new Label
            {
                Text = "Разрешение, p:",
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
            _cmbScreenRes.Items.AddRange(new object[] { "Исходное", "1080", "720", "480", "360" });
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

            // Выбор видеокарты для кодирования убран: единственный тумблер —
            // «Аппаратное ускорение (GPU)» ниже. Вкл → процесс пинится на
            // дискретную карту + аппаратные пути; выкл → авто (как в Windows).

            // Добавляем элементы в таблицу
            tblScreen.Controls.Add(lblRes, 0, 0);
            tblScreen.Controls.Add(_cmbScreenRes, 1, 0);
            tblScreen.Controls.Add(lblFps, 2, 0);
            tblScreen.Controls.Add(_cmbScreenFps, 3, 0);

            // Пункт «Аппаратное ускорение» убран: захват демки всегда идёт на
            // встройке, которая ведёт дисплей (пин на дискретку ронял fps 60→15 на
            // Optimus-ноутбуках, а NVENC в FFI всё равно нет). Управлять нечем.

            _chkLightTheme = new CheckBox
            {
                Text = "Светлая тема оформления",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                AutoSize = true,
                Location = new Point(14, 122),
                Cursor = Cursors.Hand
            };
            var lblThemeHint = new Label
            {
                Text = "Переключение применится после перезапуска приложения.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(140, 142, 146),
                AutoSize = false,
                Size = new Size(430, 16),
                Location = new Point(16, 144)
            };

            // Все мониторы в выборе демонстрации (WGC): DXGI-захват на мульти-GPU
            // системах показывает только экраны «своего» GPU — часть мониторов
            // пропадает из диалога. WGC видит все, но захват ограничен ~30 fps.
            _chkAllMonitors = new CheckBox
            {
                Text = "Показывать все мониторы в выборе демонстрации",
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 221, 222),
                BackColor = Color.FromArgb(47, 49, 54),
                AutoSize = true,
                Location = new Point(14, 166),
                Cursor = Cursors.Hand
            };
            var lblAllMonHint = new Label
            {
                Text = "Включите, если видны не все экраны (мульти-GPU). Захват до ~30 fps. Нужен перезапуск.",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(140, 142, 146),
                AutoSize = false,
                Size = new Size(430, 16),
                Location = new Point(16, 188)
            };
            pnlScreen.Height = 216;

            pnlScreen.Controls.Add(lblScreenTitle);
            pnlScreen.Controls.Add(tblScreen);
            pnlScreen.Controls.Add(_chkLightTheme);
            pnlScreen.Controls.Add(lblThemeHint);
            pnlScreen.Controls.Add(_chkAllMonitors);
            pnlScreen.Controls.Add(lblAllMonHint);

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

            // Колесо мыши над ползунками/списками НЕ меняет их значение, а
            // прокручивает страницу настроек.
            DisableWheelOnInputs(scrollPanel, scrollPanel);

            _levelTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _levelTimer.Tick += (s, e) => UpdateLevelBar();
        }
    }
}
