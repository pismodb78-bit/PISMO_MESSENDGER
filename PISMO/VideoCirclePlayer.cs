using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Контрол для проигрывания видео-кружочка (круглое видео + синхронный звук)
    /// внутри пузыря сообщения. Клик по кружочку запускает/ставит на паузу.
    /// </summary>
    public class VideoCirclePlayer : Panel
    {
        private VideoCircleCodec.DecodedVideo _video;
        private int _frameIndex = 0;
        private System.Windows.Forms.Timer _playTimer;
        private bool _isPlaying = false;

        /// <summary>Откуда взять байты, если кружок ещё не раскрыт.</summary>
        private Func<byte[]> _loader;
        private bool _loading;
        private readonly int _diameter;

        private WaveOutEvent _waveOut;
        private MemoryStream _audioMs;

        private Label _playIcon;
        private Label _durationLabel;

        /// <summary>
        /// Ленивый кружок: в ленте — только круг с кнопкой, байты и кадры
        /// появляются по нажатию.
        /// </summary>
        /// <remarks>
        /// Раскрытый кружок — это ВСЕ его кадры картинками в памяти. Десяток
        /// кружков в переписке — сотни мегабайт, и набирались они при каждом
        /// открытии чата, хотя смотрят обычно один. Поэтому в ленте висит
        /// заглушка, а раскрывается кружок там же, где и нажимается.
        /// </remarks>
        public VideoCirclePlayer(Func<byte[]> loader, int diameter = 180)
        {
            _loader = loader;
            _diameter = diameter;

            Size = new Size(diameter, diameter);
            BackColor = Color.FromArgb(32, 34, 37);
            Cursor = Cursors.Hand;
            Region = new Region(GetCirclePath(diameter));

            _playIcon = new Label
            {
                Text = "▶",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(diameter, diameter),
                Location = new Point(0, 0)
            };
            _durationLabel = new Label
            {
                Text = "кружок",
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(120, 0, 0, 0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(54, 20),
                Location = new Point((diameter - 54) / 2, diameter - 26)
            };
            Controls.Add(_playIcon);
            Controls.Add(_durationLabel);

            _playIcon.Click += (s, e) => Unfold();
            Click += (s, e) => Unfold();
        }

        /// <summary>Достаёт байты и собирает настоящий плеер поверх заглушки.</summary>
        private async void Unfold()
        {
            if (_loading || _loader == null) return;
            _loading = true;
            _playIcon.Text = "…";

            var loader = _loader;
            byte[] data = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                try { data = loader(); } catch { }
            });
            if (IsDisposed) return;

            if (data == null || data.Length == 0)
            {
                _playIcon.Text = "▶";
                _durationLabel.Text = "нет";
                _loading = false;
                return;
            }

            _loader = null;
            try { Controls.Clear(); } catch { }
            try { _playIcon.Dispose(); _durationLabel.Dispose(); } catch { }
            // Кадр-превью заглушки больше не нужен — под ним настоящий плеер.
            try { var bgi = BackgroundImage; BackgroundImage = null; bgi?.Dispose(); } catch { }
            Build(data, _diameter);
            TogglePlay();
        }

        /// <summary>
        /// Первый кадр и длительность на заглушке. Раскладывать весь кружок
        /// ради этого незачем: хватает одного кадра из начала файла, и достаётся
        /// он в фоне.
        /// </summary>
        public void ShowPreview(byte[] data)
        {
            if (data == null || data.Length == 0 || _loader == null) return;
            bool started = false;
            void Start()
            {
                if (started) return;
                started = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    Bitmap frame = null; double sec = 0;
                    try { (frame, sec) = VideoCircleCodec.Peek(data); } catch { }
                    if (frame == null) return;
                    try
                    {
                        if (IsDisposed || !IsHandleCreated) { frame.Dispose(); return; }
                        BeginInvoke(new Action(() =>
                        {
                            // Уже раскрыт или умер — превью ни к чему.
                            if (IsDisposed || _loader == null) { frame.Dispose(); return; }
                            var old = BackgroundImage;
                            BackgroundImageLayout = ImageLayout.Zoom;
                            BackgroundImage = frame;
                            try { old?.Dispose(); } catch { }
                            if (sec > 0 && _durationLabel != null && !_durationLabel.IsDisposed)
                                _durationLabel.Text = $"{(int)sec / 60}:{(int)sec % 60:00}";
                        }));
                    }
                    catch { frame.Dispose(); }
                });
            }
            if (IsHandleCreated) Start();
            else HandleCreated += (s, e) => Start();
            Disposed += (s, e) => { try { BackgroundImage?.Dispose(); } catch { } };
        }

        public VideoCirclePlayer(byte[] videoData, int diameter = 180)
        {
            Build(videoData, diameter);
        }

        private void Build(byte[] videoData, int diameter)
        {
            _video = VideoCircleCodec.Decode(videoData);

            Size = new Size(diameter, diameter);
            BackColor = Color.FromArgb(32, 34, 37);
            Cursor = Cursors.Hand;
            Region = new Region(GetCirclePath(diameter));

            if (_video.Frames.Count > 0)
            {
                var bg = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Image = (Bitmap)_video.Frames[0].Clone()
                };
                Controls.Add(bg);
                _pictureBox = bg;
            }

            _playIcon = new Label
            {
                Text = "▶",
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(diameter, diameter),
                Location = new Point(0, 0)
            };

            double totalSeconds = _video.Fps > 0 ? (double)_video.Frames.Count / _video.Fps : 0;
            _durationLabel = new Label
            {
                Text = $"{(int)totalSeconds / 60:00}:{(int)totalSeconds % 60:00}",
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(120, 0, 0, 0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(54, 20),
                Location = new Point((diameter - 54) / 2, diameter - 26)
            };

            Controls.Add(_playIcon);
            Controls.Add(_durationLabel);

            _playIcon.Click += (s, e) => TogglePlay();
            Click += (s, e) => TogglePlay();
            if (_pictureBox != null) _pictureBox.Click += (s, e) => TogglePlay();

            int intervalMs = _video.Fps > 0 ? 1000 / _video.Fps : 100;
            _playTimer = new System.Windows.Forms.Timer { Interval = Math.Max(intervalMs, 1) };
            _playTimer.Tick += PlayTimer_Tick;
        }

        private PictureBox _pictureBox;

        private static GraphicsPath GetCirclePath(int size)
        {
            var path = new GraphicsPath();
            path.AddEllipse(0, 0, size, size);
            return path;
        }

        // ════════════════════════════════════════════════════════════
        //  ВОСПРОИЗВЕДЕНИЕ
        // ════════════════════════════════════════════════════════════
        private void TogglePlay()
        {
            if (_isPlaying) Pause();
            else Play();
        }

        private void Play()
        {
            if (_video.Frames.Count == 0) return;

            _isPlaying = true;
            _playIcon.Visible = false;

            if (_frameIndex >= _video.Frames.Count) _frameIndex = 0;

            // Звук
            if (_video.AudioWav.Length > 0)
            {
                try
                {
                    StopAudio();
                    _audioMs = new MemoryStream(_video.AudioWav);
                    var reader = new WaveFileReader(_audioMs);
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(new PISMO.Native.TapWaveProvider(reader));
                    _waveOut.PlaybackStopped += (s, e) => reader.Dispose();
                    _waveOut.Play();
                }
                catch { /* воспроизведение без звука */ }
            }

            _playTimer.Start();
        }

        private void Pause()
        {
            _isPlaying = false;
            _playTimer.Stop();
            _playIcon.Text = "▶";
            _playIcon.Visible = true;
            StopAudio();
        }

        private void StopAudio()
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                _audioMs?.Dispose();
                _audioMs = null;
            }
            catch { }
        }

        private void PlayTimer_Tick(object sender, EventArgs e)
        {
            if (_video.Frames.Count == 0) return;

            _frameIndex++;
            if (_frameIndex >= _video.Frames.Count)
            {
                // Конец видео
                _frameIndex = 0;
                Pause();
                if (_pictureBox != null)
                    _pictureBox.Image = (Bitmap)_video.Frames[0].Clone();
                return;
            }

            if (_pictureBox != null)
            {
                var old = _pictureBox.Image;
                _pictureBox.Image = (Bitmap)_video.Frames[_frameIndex].Clone();
                if (old != _video.Frames[0]) old?.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _playTimer?.Stop();
                _playTimer?.Dispose();
                StopAudio();

                foreach (var f in _video.Frames)
                    f.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
