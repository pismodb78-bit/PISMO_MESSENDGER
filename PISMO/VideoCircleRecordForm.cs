using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Окно записи видео-кружочка.
    /// Лимит: 5 минут. Прогресс-бар + предупреждения о размере файла.
    /// </summary>
    public class VideoCircleRecordForm : Form
    {
        private const int CircleSize  = 320;
        private const int Fps         = 8;      // кадров в секунду
        private const int MaxSeconds  = 300;    // 5 минут максимум
        private const int WarnSeconds = 270;    // предупреждение за 30 сек до конца

        // Примерный размер: JPEG 320×320 quality=70 ≈ 12 КБ/кадр; аудио 16kHz mono ≈ 32 КБ/с
        private const long ApproxFrameBytes      = 12_000;
        private const long ApproxAudioBytesPerSec = 32_000;
        private const long MaxRecommendedBytes   = 30 * 1024 * 1024; // 30 МБ — мягкий лимит

        private PictureBox  _pbPreview;
        private Label       _lblTimer;
        private Label       _lblHint;
        private Label       _lblSize;
        private ProgressBar _pbProgress;
        private Button      _btnRecord;
        private Button      _btnCancel;

        private FilterInfoCollection _videoDevices;
        private VideoCaptureDevice   _videoSource;
        private Bitmap               _lastFrame;

        private WaveInEvent    _waveIn;
        private MemoryStream   _audioStream;
        private WaveFileWriter _waveWriter;

        private readonly List<Bitmap>             _recordedFrames = new();
        private System.Windows.Forms.Timer        _captureTimer;
        private System.Windows.Forms.Timer        _uiTimer;
        private DateTime _recordStart;
        private bool     _isRecording = false;
        private bool     _warnShown   = false;  // флаг: предупреждение о размере уже показано

        /// <summary>Готовый запакованный кружочек (заполняется при успешной записи).</summary>
        public byte[] ResultVideoData { get; private set; }

        public VideoCircleRecordForm()
        {
            BuildUi();
            StartCameraPreview();
        }

        // ════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text            = "PISMO — Видео-кружочек";
            ClientSize      = new Size(360, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            // ── Заголовок ────────────────────────────────────────────
            var lblTitle = new Label
            {
                Text      = "🔵 Видео-кружочек",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            // ── Превью камеры ─────────────────────────────────────────
            _pbPreview = new PictureBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                SizeMode  = PictureBoxSizeMode.Zoom,
                Location  = new Point((ClientSize.Width - CircleSize) / 2, 52),
                Size      = new Size(CircleSize, CircleSize),
                Region    = new Region(GetCirclePath(CircleSize))
            };

            // ── Таймер ───────────────────────────────────────────────
            _lblTimer = new Label
            {
                Text      = "00:00 / 05:00",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(100, 386)
            };

            // ── Прогресс-бар ─────────────────────────────────────────
            _pbProgress = new ProgressBar
            {
                Location = new Point(20, 416),
                Size     = new Size(320, 8),
                Minimum  = 0,
                Maximum  = MaxSeconds,
                Value    = 0,
                Style    = ProgressBarStyle.Continuous
            };

            // ── Размер файла ──────────────────────────────────────────
            _lblSize = new Label
            {
                Text      = "Лимит: 5 мин • макс. ~30 МБ рекомендуется",
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(20, 430),
                Size      = new Size(320, 18)
            };

            // ── Подсказка ─────────────────────────────────────────────
            _lblHint = new Label
            {
                Text      = "Нажмите «Записать», чтобы начать (макс. 5 мин)",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(20, 452),
                Size      = new Size(320, 20)
            };

            // ── Кнопки ───────────────────────────────────────────────
            _btnRecord = new Button
            {
                Text      = "● Записать",
                BackColor = Color.FromArgb(240, 71, 71),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 486),
                Size      = new Size(160, 42),
                Cursor    = Cursors.Hand
            };
            _btnRecord.FlatAppearance.BorderSize = 0;
            _btnRecord.MouseEnter += (s, e) => _btnRecord.BackColor =
                _isRecording ? Color.FromArgb(180, 60, 60) : Color.FromArgb(200, 55, 55);
            _btnRecord.MouseLeave += (s, e) => _btnRecord.BackColor =
                _isRecording ? Color.FromArgb(79, 84, 92) : Color.FromArgb(240, 71, 71);
            _btnRecord.Click += BtnRecord_Click;

            _btnCancel = new Button
            {
                Text      = "Отмена",
                BackColor = Color.FromArgb(79, 84, 92),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Location  = new Point(190, 486),
                Size      = new Size(150, 42),
                Cursor    = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.AddRange(new Control[]
            {
                lblTitle, _pbPreview,
                _lblTimer, _pbProgress, _lblSize, _lblHint,
                _btnRecord, _btnCancel
            });

            // ── Таймеры ───────────────────────────────────────────────
            _captureTimer = new System.Windows.Forms.Timer { Interval = 1000 / Fps };
            _captureTimer.Tick += CaptureTimer_Tick;

            _uiTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _uiTimer.Tick += UiTimer_Tick;
        }

        private static GraphicsPath GetCirclePath(int size)
        {
            var path = new GraphicsPath();
            path.AddEllipse(0, 0, size, size);
            return path;
        }

        // ════════════════════════════════════════════════════════════
        //  КАМЕРА — ПРЕВЬЮ
        // ════════════════════════════════════════════════════════════
        private void StartCameraPreview()
        {
            try
            {
                _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (_videoDevices.Count == 0)
                {
                    _lblHint.Text      = "Камера не найдена.";
                    _btnRecord.Enabled = false;
                    return;
                }

                string moniker = null;
                if (!string.IsNullOrWhiteSpace(DeviceSettings.CameraName))
                    foreach (FilterInfo dev in _videoDevices)
                        if (dev.Name == DeviceSettings.CameraName)
                        { moniker = dev.MonikerString; break; }

                moniker ??= _videoDevices[0].MonikerString;

                _videoSource = new VideoCaptureDevice(moniker);
                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.Start();
            }
            catch (Exception ex)
            {
                _lblHint.Text      = "Ошибка камеры: " + ex.Message;
                _btnRecord.Enabled = false;
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                var frame = (Bitmap)eventArgs.Frame.Clone();
                lock (this)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = frame;
                }
                if (_pbPreview.IsHandleCreated)
                    _pbPreview.Invoke(() =>
                    {
                        var old = _pbPreview.Image;
                        _pbPreview.Image = (Bitmap)frame.Clone();
                        old?.Dispose();
                    });
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  ЗАПИСЬ
        // ════════════════════════════════════════════════════════════
        private void BtnRecord_Click(object sender, EventArgs e)
        {
            if (_isRecording)
                FinishRecording(save: true);
            else
                StartRecording();
        }

        private void StartRecording()
        {
            _recordedFrames.Clear();
            _recordStart  = DateTime.Now;
            _isRecording  = true;
            _warnShown    = false;

            _btnRecord.Text      = "■ Стоп";
            _btnRecord.BackColor = Color.FromArgb(79, 84, 92);
            _lblHint.Text        = "Идёт запись… Нажмите «Стоп» для завершения.";
            _lblSize.ForeColor   = Color.FromArgb(114, 118, 125);
            _lblSize.Text        = "Примерный размер: 0 КБ";
            _btnCancel.Enabled   = false;

            // Аудио
            try
            {
                _audioStream = new MemoryStream();
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };

                if (DeviceSettings.MicrophoneIndex >= 0 &&
                    DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                    _waveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;

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
            }
            catch { _waveIn = null; }

            _captureTimer.Start();
            _uiTimer.Start();
        }

        private void CaptureTimer_Tick(object sender, EventArgs e)
        {
            if ((DateTime.Now - _recordStart).TotalSeconds >= MaxSeconds)
            {
                FinishRecording(save: true);
                return;
            }

            Bitmap snapshot = null;
            lock (this)
            {
                if (_lastFrame != null)
                    snapshot = (Bitmap)_lastFrame.Clone();
            }

            if (snapshot != null)
            {
                var square = CropToSquare(snapshot);
                snapshot.Dispose();
                _recordedFrames.Add(square);
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!_isRecording) return;

            var elapsed    = DateTime.Now - _recordStart;
            int elapsedSec = (int)elapsed.TotalSeconds;
            int remaining  = MaxSeconds - elapsedSec;

            // ── Таймер ───────────────────────────────────────────────
            _lblTimer.Text = $"{elapsed:mm\\:ss} / {TimeSpan.FromSeconds(MaxSeconds):mm\\:ss}";

            // ── Прогресс-бар ─────────────────────────────────────────
            _pbProgress.Value = Math.Min(elapsedSec, MaxSeconds);

            // Меняем цвет прогресса через Win32 (зелёный → жёлтый → красный)
            if (elapsedSec < WarnSeconds - 60)
                NativeMethods.SetProgressBarColor(_pbProgress, NativeMethods.PbColor.Green);
            else if (elapsedSec < WarnSeconds)
                NativeMethods.SetProgressBarColor(_pbProgress, NativeMethods.PbColor.Yellow);
            else
                NativeMethods.SetProgressBarColor(_pbProgress, NativeMethods.PbColor.Red);

            // ── Примерный размер ─────────────────────────────────────
            long approxBytes = _recordedFrames.Count * ApproxFrameBytes
                             + elapsedSec * ApproxAudioBytesPerSec;
            string sizeStr = approxBytes < 1024 * 1024
                ? $"{approxBytes / 1024} КБ"
                : $"{approxBytes / 1024.0 / 1024.0:F1} МБ";

            if (approxBytes > MaxRecommendedBytes && !_warnShown)
            {
                // Мягкое предупреждение (не блокирует, просто меняет текст)
                _lblSize.ForeColor = Color.FromArgb(240, 71, 71);
                _lblSize.Text      = $"⚠ ~{sizeStr} — файл может не загрузиться в БД!";
                _warnShown = true;
            }
            else if (!_warnShown)
            {
                bool nearLimit = approxBytes > MaxRecommendedBytes * 0.75;
                _lblSize.ForeColor = nearLimit
                    ? Color.FromArgb(250, 166, 26)
                    : Color.FromArgb(114, 118, 125);
                _lblSize.Text = $"Примерный размер: ~{sizeStr}";
            }

            // ── Предупреждение за 30 сек до конца ────────────────────
            if (remaining <= 30)
            {
                _lblTimer.ForeColor = Color.FromArgb(240, 71, 71);
                _lblHint.Text       = $"⚠ Осталось {remaining} сек — запись остановится автоматически!";
            }
            else if (remaining <= 60)
            {
                _lblTimer.ForeColor = Color.FromArgb(250, 166, 26);
                _lblHint.Text       = $"Осталось {remaining} сек до конца записи";
            }
        }

        private static Bitmap CropToSquare(Bitmap src)
        {
            int side = Math.Min(src.Width, src.Height);
            int x = (src.Width - side) / 2;
            int y = (src.Height - side) / 2;

            var cropped = new Bitmap(side, side);
            using var g = Graphics.FromImage(cropped);
            g.DrawImage(src, new Rectangle(0, 0, side, side),
                             new Rectangle(x, y, side, side), GraphicsUnit.Pixel);
            return cropped;
        }

        private static byte[] ApplyGain(byte[] buffer, int bytesRecorded, float gain)
        {
            var result = new byte[bytesRecorded];
            Array.Copy(buffer, result, bytesRecorded);
            for (int i = 0; i + 1 < bytesRecorded; i += 2)
            {
                short sample    = (short)((result[i + 1] << 8) | result[i]);
                int   amplified = Math.Clamp((int)(sample * gain), short.MinValue, short.MaxValue);
                result[i]     = (byte)(amplified & 0xFF);
                result[i + 1] = (byte)((amplified >> 8) & 0xFF);
            }
            return result;
        }

        private void FinishRecording(bool save)
        {
            // Запускаем async-версию — encode идёт в фоне, UI не замерзает
            _ = FinishRecordingAsync(save);
        }

        private async System.Threading.Tasks.Task FinishRecordingAsync(bool save)
        {
            _isRecording = false;
            _captureTimer.Stop();
            _uiTimer.Stop();

            // 1. Останавливаем аудио на UI-потоке
            byte[] wavBytes = Array.Empty<byte>();
            try
            {
                if (_waveIn != null)
                {
                    _waveIn.StopRecording();
                    // Небольшая пауза чтобы DataAvailable успел дописать последний буфер
                    await System.Threading.Tasks.Task.Delay(150);
                    _waveWriter?.Flush();
                    wavBytes = _audioStream?.ToArray() ?? Array.Empty<byte>();
                    _waveWriter?.Dispose();
                    _waveIn.Dispose();
                    _waveIn = null;
                }
            }
            catch { }

            if (!save || _recordedFrames.Count == 0)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            // 2. Блокируем кнопки, запускаем анимацию ожидания
            _btnRecord.Enabled = false;
            _btnCancel.Enabled = false;
            _lblSize.ForeColor = Color.FromArgb(185, 187, 190);
            _lblSize.Text      = "Кодирование…";

            var dotTimer = new System.Windows.Forms.Timer { Interval = 400 };
            int dots = 0;
            dotTimer.Tick += (s, e) =>
            {
                dots = (dots + 1) % 4;
                _lblHint.Text = "⏳ Упаковка кадров" + new string('.', dots);
            };
            dotTimer.Start();

            // 3. Encode в фоновом потоке — UI не замерзает
            var framesCopy = new List<Bitmap>(_recordedFrames);
            var wavCopy    = wavBytes;
            byte[] encoded    = null;
            string encodeErr  = null;

            await System.Threading.Tasks.Task.Run(() =>
            {
                try   { encoded  = VideoCircleCodec.Encode(framesCopy, wavCopy, Fps); }
                catch (Exception ex) { encodeErr = ex.Message; }
            });

            dotTimer.Stop();
            dotTimer.Dispose();

            // 4. Проверяем результат на UI-потоке
            if (encodeErr != null)
            {
                MessageBox.Show("Ошибка упаковки видео:\n" + encodeErr,
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            ResultVideoData = encoded;
            long   sz    = encoded.LongLength;
            string szStr = sz < 1024 * 1024
                ? $"{sz / 1024} КБ"
                : $"{sz / 1024.0 / 1024.0:F1} МБ";

            if (sz > 64L * 1024 * 1024)
            {
                MessageBox.Show(
                    $"Видео весит {szStr} — превышен лимит сервера (64 МБ).\n" +
                    "Запишите более короткое видео.",
                    "PISMO — слишком большой файл",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.Cancel;
            }
            else if (sz > MaxRecommendedBytes)
            {
                var res = MessageBox.Show(
                    $"Видео весит {szStr}.\n" +
                    "Файл большой — отправка может занять время.\n\n" +
                    "Отправить всё равно?",
                    "PISMO — большой файл",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                DialogResult = res == DialogResult.Yes
                    ? DialogResult.OK : DialogResult.Cancel;
            }
            else
            {
                DialogResult = DialogResult.OK;
            }

            Close();
        }

        // ════════════════════════════════════════════════════════════
        //  ОЧИСТКА
        // ════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                _captureTimer?.Stop();   _captureTimer?.Dispose();
                _uiTimer?.Stop();        _uiTimer?.Dispose();

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

                if (_waveIn != null)
                {
                    try { _waveIn.StopRecording(); } catch { }
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                _waveWriter?.Dispose();
                _lastFrame?.Dispose();
                foreach (var f in _recordedFrames) f.Dispose();
            }
            catch { }

            base.OnFormClosing(e);
        }
    }
}
