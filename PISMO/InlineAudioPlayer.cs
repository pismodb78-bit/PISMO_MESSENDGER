using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Плеер голосовых и музыки прямо в пузыре сообщения: кнопка ▶/⏸, полоса
    /// перемотки со временем и громкость. Рисуется своими руками поверх NAudio —
    /// панель управления Chromium в маленьком пузыре показывалась пустой.
    ///
    /// Декодирование — через Media Foundation (mp3, m4a/aac, wma, flac…), с
    /// откатом на обычный WAV-ридер: голосовые у нас пишутся именно в WAV.
    ///
    /// ВАЖНО про автообновление: лента перерисовывается целиком раз в несколько
    /// секунд, и вместе с ней пересоздаются эти контролы. Поэтому само
    /// воспроизведение живёт не в контроле, а в статической сессии, привязанной
    /// к сообщению: заново созданный плеер подхватывает её и продолжает играть с
    /// той же секунды. Иначе звук обрывался на каждом обновлении, а открытие и
    /// закрытие звукового устройства на каждой отрисовке заметно подлагивало.
    /// </summary>
    internal sealed class InlineAudioPlayer : Panel
    {
        // ── Сессия воспроизведения (переживает перерисовку ленты) ────────────
        private sealed class Session
        {
            public WaveOutEvent Out;
            public WaveStream Reader;
            public MemoryStream Stream;
            public float Volume = 0.8f;

            public void Dispose()
            {
                try { Out?.Dispose(); } catch { }
                try { Reader?.Dispose(); } catch { }
                try { Stream?.Dispose(); } catch { }
                Out = null; Reader = null; Stream = null;
            }
        }

        private static readonly Dictionary<string, Session> _sessions = new();

        private static void DropSession(string key)
        {
            if (key == null) return;
            if (_sessions.TryGetValue(key, out var s)) { s.Dispose(); _sessions.Remove(key); }
        }

        // ── Состояние контрола ──────────────────────────────────────────────
        private readonly string _fileName;
        private readonly Func<byte[]> _loader;
        private readonly string _key;
        private byte[] _data;
        private Session _sess;

        private System.Windows.Forms.Timer _tick;
        private bool _loading, _seeking, _volDrag;
        private float _volume = 0.8f;
        private string _error;

        // Геометрия: кнопка слева, дальше полоса, справа время/имя и громкость.
        private const int BtnSize = 28;
        private const int Pad = 8;
        private const int VolW = 54;
        private const int TimeW = 110;

        /// <param name="key">Идентификатор сообщения — по нему воспроизведение
        /// находит себя после перерисовки. null — сессия не сохраняется.</param>
        public InlineAudioPlayer(byte[] data, Func<byte[]> loader, string fileName, int width,
                                 string key = null)
        {
            _data = data;
            _loader = loader;
            _fileName = string.IsNullOrWhiteSpace(fileName) ? "Аудио" : fileName;
            _key = key;

            Size = new Size(width, 44);
            BackColor = Color.FromArgb(30, 31, 34);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // Продолжаем то, что уже играло до перерисовки.
            if (_key != null && _sessions.TryGetValue(_key, out var live))
            {
                _sess = live;
                _volume = live.Volume;
                if (live.Out != null && live.Out.PlaybackState == PlaybackState.Playing) EnsureTick(true);
            }
        }

        private void EnsureTick(bool start)
        {
            _tick ??= NewTick();
            if (start) _tick.Start(); else _tick.Stop();
        }

        private System.Windows.Forms.Timer NewTick()
        {
            var t = new System.Windows.Forms.Timer { Interval = 200 };
            t.Tick += (s, e) =>
            {
                // Дошли до конца — возвращаем полосу в исходный вид: подпись
                // «Голосовое» / имя файла вместо «0:03 / 0:03».
                if (_sess?.Out != null && _sess.Out.PlaybackState == PlaybackState.Stopped)
                { Reset(); return; }
                if (!_seeking) Invalidate();
            };
            return t;
        }

        private void Reset()
        {
            EnsureTick(false);
            DropSession(_key);
            if (_key == null) _sess?.Dispose();
            _sess = null;
            Invalidate();
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

            bool playing = _sess?.Out != null && _sess.Out.PlaybackState == PlaybackState.Playing;

            var btn = BtnRect;
            using (var b = new SolidBrush(Color.FromArgb(88, 101, 242))) g.FillEllipse(b, btn);
            if (_loading)
            {
                using var p = new Pen(Color.White, 2);
                g.DrawArc(p, Rectangle.Inflate(btn, -8, -8), (Environment.TickCount / 3) % 360, 100);
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
                if (_sess?.Reader != null)
                {
                    dur = _sess.Reader.TotalTime.TotalSeconds;
                    pos = _sess.Reader.CurrentTime.TotalSeconds;
                }
            }
            catch { }

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

            // Пока не играем — подпись (имя файла / «Голосовое»), в процессе — время.
            using (var f = new Font("Segoe UI", 8f))
            using (var b = new SolidBrush(Color.FromArgb(190, 192, 198)))
            {
                string t = dur > 0 ? $"{Fmt(pos)} / {Fmt(dur)}" : Path.GetFileName(_fileName);
                var rect = new RectangleF(bar.Right + Pad, Height / 2 - 8, TimeW, 16);
                using var sf = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(t, f, b, rect, sf);
            }

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
            _seeking = false; _volDrag = false; Capture = false;
        }

        private void SeekTo(int x)
        {
            try
            {
                var rd = _sess?.Reader;
                if (rd == null || rd.TotalTime.TotalSeconds <= 0) return;
                var bar = BarRect;
                double frac = Math.Min(1.0, Math.Max(0.0, (x - bar.X) / (double)bar.Width));
                rd.CurrentTime = TimeSpan.FromSeconds(rd.TotalTime.TotalSeconds * frac);
                Invalidate();
            }
            catch { }
        }

        private void SetVolume(int x)
        {
            var vol = VolRect;
            _volume = (float)Math.Min(1.0, Math.Max(0.0, (x - vol.X) / (double)vol.Width));
            try
            {
                if (_sess != null) { _sess.Volume = _volume; if (_sess.Out != null) _sess.Out.Volume = _volume; }
            }
            catch { }
            Invalidate();
        }

        // ── Воспроизведение ──────────────────────────────────────────────────
        private async void Toggle()
        {
            if (_loading) return;

            if (_sess?.Out != null)
            {
                if (_sess.Out.PlaybackState == PlaybackState.Playing) { _sess.Out.Pause(); EnsureTick(false); }
                else { _sess.Out.Play(); EnsureTick(true); }
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

            // Открытие декодера и звукового устройства занимает десятки
            // миллисекунд — в UI-потоке это заметный рывок ленты при нажатии.
            _loading = true; Invalidate();
            var bytes = _data; float vol = _volume;
            Session made = null; string err = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sess = new Session { Volume = vol };
                    sess.Stream = new MemoryStream(bytes, writable: false);
                    sess.Reader = OpenReader(sess.Stream);
                    sess.Out = new WaveOutEvent();
                    sess.Out.Init(sess.Reader);
                    sess.Out.Volume = vol;
                    sess.Out.Play();
                    made = sess;
                }
                catch (Exception ex) { err = ex.Message; }
            });
            _loading = false;

            if (IsDisposed) { made?.Dispose(); return; }
            if (made == null) { _error = "Не удалось воспроизвести: " + err; Invalidate(); return; }

            _sess = made;
            if (_key != null) { DropSession(_key); _sessions[_key] = made; }
            EnsureTick(true);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _tick?.Stop(); _tick?.Dispose(); } catch { }
                _tick = null;
                // Звук НЕ останавливаем: сессия переживает перерисовку ленты и
                // будет подхвачена новым контролом. Без ключа держать её негде.
                if (_key == null) { _sess?.Dispose(); _sess = null; }
            }
            base.Dispose(disposing);
        }
    }
}
