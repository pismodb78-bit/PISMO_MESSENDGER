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
        private readonly VideoCircleCodec.DecodedVideo _video;
        private int _frameIndex = 0;
        private readonly System.Windows.Forms.Timer _playTimer;
        private bool _isPlaying = false;

        private WaveOutEvent _waveOut;
        private MemoryStream _audioMs;

        private readonly Label _playIcon;
        private readonly Label _durationLabel;

        public VideoCirclePlayer(byte[] videoData, int diameter = 180)
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
                    _waveOut.Init(reader);
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
