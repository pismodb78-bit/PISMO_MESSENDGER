using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Плеер музыки прямо в пузыре сообщения: кнопка ▶/⏸, полоса перемотки со
    /// временем и громкость. Рисуется своими руками поверх NAudio — WebView2
    /// здесь не годится: его собственная панель управления в маленьком пузыре
    /// показывалась пустым чёрным прямоугольником.
    ///
    /// Декодирование — через Media Foundation (mp3, m4a/aac, wma, flac…), с
    /// откатом на обычный WAV-ридер: голосовые у нас пишутся именно в WAV.
    /// </summary>
    internal sealed class InlineAudioPlayer : Panel
    {
        private readonly string _fileName;
        private readonly Func<byte[]> _loader;
        private byte[] _data;

        private WaveOutEvent _out;
        private WaveStream _reader;
        private MemoryStream _ms;
        private readonly System.Windows.Forms.Timer _tick;

        private bool _loading, _seeking, _volDrag;
        private float _volume = 0.8f;
        private string _error;

        // Геометрия: кнопка слева, дальше полоса, справа время и громкость.
        private const int BtnSize = 28;
        private const int Pad = 8;
        private const int VolW = 54;
        private const int TimeW = 82;

        public InlineAudioPlayer(byte[] data, Func<byte[]> loader, string fileName, int width)
        {
            _data = data;
            _loader = loader;
            _fileName = fileName ?? "audio";

            Size = new Size(width, 44);
            BackColor = Color.FromArgb(30, 31, 34);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _tick = new System.Windows.Forms.Timer { Interval = 200 };
            _tick.Tick += (s, e) => { if (!_seeking) Invalidate(); };
        }

        // ── Раскладка ────────────────────────────────────────────────────────
        private Rectangle BtnRect => new(Pad, (Height - BtnSize) / 2, BtnSize, BtnSize);
        private Rectangle VolRect => new(Width - Pad - VolW, Height / 2 - 3, VolW, 6);
        private Rectangle BarRect
        {
            get
            {
                int left = BtnRect.Right + Pad;
                int right = VolRect.Left - Pad - TimeW;
                return new Rectangle(left, Height / 2 - 3, Math.Max(20, right - left), 6);
            }
        }

        // ── Отрисовка ────────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            bool playing = _out != null && _out.PlaybackState == PlaybackState.Playing;

            // Кнопка
            var btn = BtnRect;
            using (var b = new SolidBrush(Color.FromArgb(88, 101, 242))) g.FillEllipse(b, btn);
            if (_loading)
            {
                using var p = new Pen(Color.White, 2);
                g.DrawArc(p, Rectangle.Inflate(btn, -8, -8),
                          (Environment.TickCount / 3) % 360, 100);
            }
            else if (playing)
            {
                using var b = new SolidBrush(Color.White);
                g.FillRectangle(b, btn.X + 10, btn.Y + 8, 3, 12);
                g.FillRectangle(b, btn.X + 16, btn.Y + 8, 3, 12);
            }
            else
            {
                using var b = new SolidBrush(Color.White);
                g.FillPolygon(b, new[]
                {
                    new Point(btn.X + 11, btn.Y + 8),
                    new Point(btn.X + 21, btn.Y + 14),
                    new Point(btn.X + 11, btn.Y + 20)
                });
            }

            // Ошибка вместо полосы — чтобы не гадать, почему тишина.
            if (_error != null)
            {
                using var f = new Font("Segoe UI", 8.5f);
                using var b = new SolidBrush(Color.FromArgb(240, 180, 160));
                g.DrawString(_error, f, b, BtnRect.Right + Pad, Height / 2 - 8);
                return;
            }

            double pos = 0, dur = 0;
            try
            {
                if (_reader != null)
                {
                    dur = _reader.TotalTime.TotalSeconds;
                    pos = _reader.CurrentTime.TotalSeconds;
                }
            }
            catch { }

            // Полоса воспроизведения
            var bar = BarRect;
            using (var track = new SolidBrush(Color.FromArgb(58, 60, 66)))
                g.FillRectangle(track, bar);
            if (dur > 0)
            {
                int w = (int)(bar.Width * Math.Min(1.0, pos / dur));
                using var done = new SolidBrush(Color.FromArgb(88, 101, 242));
                g.FillRectangle(done, bar.X, bar.Y, w, bar.Height);
                using var knob = new SolidBrush(Color.White);
                g.FillEllipse(knob, bar.X + w - 5, bar.Y - 3, 10, 10);
            }

            // Время
            using (var f = new Font("Segoe UI", 8f))
            using (var b = new SolidBrush(Color.FromArgb(190, 192, 198)))
            {
                string t = dur > 0 ? $"{Fmt(pos)} / {Fmt(dur)}" : Path.GetFileName(_fileName);
                var rect = new RectangleF(bar.Right + Pad, Height / 2 - 8, TimeW, 16);
                var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                g.DrawString(t, f, b, rect, sf);
            }

            // Громкость
            var vol = VolRect;
            using (var track = new SolidBrush(Color.FromArgb(58, 60, 66)))
                g.FillRectangle(track, vol);
            int vw = (int)(vol.Width * _volume);
            using (var done = new SolidBrush(Color.FromArgb(140, 146, 160)))
                g.FillRectangle(done, vol.X, vol.Y, vw, vol.Height);
            using (var knob = new SolidBrush(Color.FromArgb(225, 227, 232)))
                g.FillEllipse(knob, vol.X + vw - 4, vol.Y - 2, 9, 9);
        }

        private static string Fmt(double sec)
        {
            if (double.IsNaN(sec) || sec < 0) sec = 0;
            var t = TimeSpan.FromSeconds(sec);
            return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                               : $"{t.Minutes}:{t.Seconds:00}";
        }

        // ── Мышь ─────────────────────────────────────────────────────────────
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (BtnRect.Contains(e.Location)) { Toggle(); return; }

            var bar = BarRect;
            if (e.Y >= bar.Y - 6 && e.Y <= bar.Bottom + 6 && e.X >= bar.X && e.X <= bar.Right)
            { _seeking = true; Capture = true; SeekTo(e.X); return; }

            var vol = VolRect;
            if (e.Y >= vol.Y - 8 && e.Y <= vol.Bottom + 8 && e.X >= vol.X - 6 && e.X <= vol.Right + 6)
            { _volDrag = true; Capture = true; SetVolume(e.X); return; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.Left) return;
            // Тянуть, а не только кликать: пока кнопка зажата, ползунок следует за
            // курсором — даже если он ушёл за пределы дорожки (значение зажимается).
            if (_seeking) SeekTo(e.X);
            else if (_volDrag) SetVolume(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _seeking = false;
            _volDrag = false;
            Capture = false;
        }

        private void SeekTo(int x)
        {
            try
            {
                if (_reader == null || _reader.TotalTime.TotalSeconds <= 0) return;
                var bar = BarRect;
                double frac = Math.Min(1.0, Math.Max(0.0, (x - bar.X) / (double)bar.Width));
                _reader.CurrentTime = TimeSpan.FromSeconds(_reader.TotalTime.TotalSeconds * frac);
                Invalidate();
            }
            catch { }
        }

        private void SetVolume(int x)
        {
            var vol = VolRect;
            _volume = (float)Math.Min(1.0, Math.Max(0.0, (x - vol.X) / (double)vol.Width));
            try { if (_out != null) _out.Volume = _volume; } catch { }
            Invalidate();
        }

        // ── Воспроизведение ──────────────────────────────────────────────────
        private async void Toggle()
        {
            if (_loading) return;

            if (_out != null)
            {
                if (_out.PlaybackState == PlaybackState.Playing) { _out.Pause(); _tick.Stop(); }
                else { _out.Play(); _tick.Start(); }
                Invalidate();
                return;
            }

            // Байты могли не грузиться вместе с лентой — читаем по нажатию.
            if (_data == null && _loader != null)
            {
                _loading = true; _error = null; Invalidate();
                var loader = _loader;
                byte[] loaded = null;
                await System.Threading.Tasks.Task.Run(() => { try { loaded = loader(); } catch { } });
                _loading = false;
                if (IsDisposed) return;
                if (loaded == null || loaded.Length == 0) { _error = "Не удалось загрузить"; Invalidate(); return; }
                _data = loaded;
            }

            try
            {
                _ms = new MemoryStream(_data, writable: false);
                _reader = OpenReader(_ms);
                _out = new WaveOutEvent();
                _out.Init(_reader);
                _out.Volume = _volume;
                _out.PlaybackStopped += (s, e) =>
                {
                    try { BeginInvoke(new Action(() => { _tick.Stop(); Invalidate(); })); } catch { }
                };
                _out.Play();
                _tick.Start();
            }
            catch (Exception ex)
            {
                Cleanup();
                _error = "Не удалось воспроизвести: " + ex.Message;
            }
            Invalidate();
        }

        /// <summary>Media Foundation тянет почти всё, что умеет сама Windows;
        /// WAV читаем напрямую — так работают наши голосовые.</summary>
        private static WaveStream OpenReader(MemoryStream ms)
        {
            try { return new StreamMediaFoundationReader(ms); }
            catch
            {
                ms.Position = 0;
                return new WaveFileReader(ms);
            }
        }

        private void Cleanup()
        {
            try { _tick.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _ms?.Dispose(); } catch { }
            _out = null; _reader = null; _ms = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Cleanup(); try { _tick.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
