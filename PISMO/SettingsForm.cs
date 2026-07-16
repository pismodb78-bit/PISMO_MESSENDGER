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
    public partial class SettingsForm : Form
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
        private CheckBox _chkHwAccel;
        private CheckBox _chkLightTheme;
        private CheckBox _chkAllMonitors;   // все мониторы в выборе демонстрации (WGC)
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
        private ComboBox _cmbGpu;           // видеокарта для кодирования демки/камеры

        public SettingsForm()
        {
            this.Load += (s, e) => { try { Theme.Apply(this); } catch { } };
            BuildUi();
            LoadDevices();
            ApplySavedSelection();
        }


        /// <summary>Делает так, что колесо мыши над TrackBar/ComboBox не меняет
        /// значение, а прокручивает страницу настроек.</summary>
        private static void DisableWheelOnInputs(Control root, Panel scroll)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TrackBar || c is ComboBox || c is NumericUpDown)
                {
                    c.MouseWheel += (s, e) =>
                    {
                        if (e is HandledMouseEventArgs he) he.Handled = true; // не менять значение
                        int y = -scroll.AutoScrollPosition.Y - e.Delta;       // прокрутить страницу
                        scroll.AutoScrollPosition = new Point(0, y);
                    };
                }
                if (c.HasChildren) DisableWheelOnInputs(c, scroll);
            }
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
            string sh = DeviceSettings.ScreenShareResolutionHeight > 0
                ? DeviceSettings.ScreenShareResolutionHeight.ToString() : "Исходное";
            for (int i = 0; i < _cmbScreenRes.Items.Count; i++)
                if (_cmbScreenRes.Items[i].ToString() == sh) { _cmbScreenRes.SelectedIndex = i; break; }
            string sf = DeviceSettings.ScreenShareFps.ToString();
            for (int i = 0; i < _cmbScreenFps.Items.Count; i++)
                if (_cmbScreenFps.Items[i].ToString() == sf) { _cmbScreenFps.SelectedIndex = i; break; }

            // Видеокарта для кодирования (auto/high/integrated/software).
            _cmbGpu.SelectedIndex = DeviceSettings.GpuEncodePref switch
            {
                "high" => 1,
                "integrated" => 2,
                "software" => 3,
                _ => 0,
            };

            // Аппаратное ускорение.
            _chkHwAccel.Checked = DeviceSettings.HardwareAcceleration;
            _chkAllMonitors.Checked = DeviceSettings.ScreenCaptureAllMonitors;

            // Тема оформления.
            _chkLightTheme.Checked = Theme.IsLight;
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
            // Тест идёт через тот же конвейер, что и звонок (WebView2 + Krisp).
            // Немодальное окно — настройки остаются кликабельными.
            try
            {
                if (_micTest != null && !_micTest.IsDisposed) { try { _micTest.Close(); } catch { } }
                _micTest = new MicTestForm(_chkNoiseSuppress.Checked, _cmbMic.SelectedItem?.ToString());
                _micTest.FormClosed += (s2, e2) => _micTest = null;
                _micTest.Show(this);
            }
            catch (Exception ex)
            {
                _lblMicStatus.Visible = true;
                _lblMicStatus.Text = "Ошибка теста: " + ex.Message;
            }
        }

        private MicTestForm _micTest;

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

            // Воспроизводим микрофон обратно (с усилением) — «слышу себя».
            // Без обращения к UI-контролам из фонового потока (это вызывало
            // InvalidOperationException про кросс-поток).
            if (_monitorBuf != null)
            {
                var outBuf = new byte[e.BytesRecorded];
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    short s = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                    int a = Math.Clamp((int)(s * gain), short.MinValue, short.MaxValue);
                    outBuf[i] = (byte)(a & 0xFF);
                    outBuf[i + 1] = (byte)((a >> 8) & 0xFF);
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
            if (_trkVoiceThreshold != null && _trkVoiceThreshold.Visible && !_chkVoiceAuto.Checked)
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
                string sr = _cmbScreenRes.SelectedItem.ToString();
                if (sr == "Исходное") DeviceSettings.ScreenShareResolutionHeight = 0;   // 0 = нативное
                else if (int.TryParse(sr, out int rh)) DeviceSettings.ScreenShareResolutionHeight = rh;
            }
            if (_cmbScreenFps.SelectedIndex >= 0)
            {
                if (int.TryParse(_cmbScreenFps.SelectedItem.ToString(), out int sfps))
                    DeviceSettings.ScreenShareFps = Math.Clamp(sfps, 1, 60);
            }

            DeviceSettings.GpuEncodePref = _cmbGpu.SelectedIndex switch
            {
                1 => "high",
                2 => "integrated",
                3 => "software",
                _ => "auto",
            };

            DeviceSettings.HardwareAcceleration = _chkHwAccel.Checked;
            DeviceSettings.ScreenCaptureAllMonitors = _chkAllMonitors.Checked;
            bool newLight = _chkLightTheme.Checked;
            DeviceSettings.ThemeMode = newLight ? "light" : "dark";

            // TURN-сервер больше не используется (звонки через LiveKit).

            DeviceSettings.Save();

            // Синхронизация с голосовым доком и активным звонком: кнопка 🎚 в доке
            // и шумодав в идущем звонке обязаны отражать новую настройку сразу.
            try { MainForm.Current?.RefreshVoiceEqState(); } catch { }
            try { MainForm.Current?.ActiveCallFormPublic()?.SetNoiseSuppressionLive(DeviceSettings.NoiseSuppression); } catch { }

            // Тема зафиксирована на старте приложения (Theme.IsLight) — если
            // выбор изменился, честно предупреждаем и предлагаем перезапуск
            // (раньше без перезапуска получалось «полусветлое» приложение).
            if (newLight != Theme.IsLight)
            {
                var r = MessageBox.Show(this,
                    "Тема применится после перезапуска приложения.\n\nПерезапустить сейчас?",
                    "PISMO — тема", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) { MainForm.RestartApplication(); return; }
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopCameraPreview();
            StopMicTest();
            try { if (_micTest != null && !_micTest.IsDisposed) _micTest.Close(); } catch { }
            _levelTimer?.Stop();
            _levelTimer?.Dispose();
            base.OnFormClosing(e);
        }

    }

}