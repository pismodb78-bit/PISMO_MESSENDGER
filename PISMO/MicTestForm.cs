using System;
using System.Drawing;
using System.Windows.Forms;
using NAudio.Wave;
using PISMO.Native;

namespace PISMO
{
    /// <summary>
    /// Тест микрофона на ТОМ ЖЕ нативном тракте, что и звонок: микрофон (NAudio) →
    /// RNNoise + спектральный шумодав (сила = ползунок «Сила шумоподавления») →
    /// обратно в наушники. Слышно ровно то, что услышат собеседники, и ползунок
    /// силы регулируется прямо здесь, вживую.
    /// </summary>
    public sealed class MicTestForm : Form
    {
        private const int SR = 48000;

        private WaveInEvent _in;
        private WaveOutEvent _out;
        private BufferedWaveProvider _buf;
        private RnnoiseDenoiser _rn;
        private SpectralDenoiser _spectral;
        private MicDenoiser _gate;   // клик-гейт против клавиатуры/мыши (на высокой силе)

        private volatile bool _noise;
        private volatile string _micLabel;
        private volatile float _gain;
        private volatile float _level;   // 0..1 для индикатора

        private readonly Panel _bar;
        private readonly Panel _fill;
        private readonly Label _st;
        private readonly System.Windows.Forms.Timer _ui;

        public MicTestForm(bool noiseSuppression, string micLabel = null)
        {
            _noise = noiseSuppression;
            _micLabel = micLabel ?? "";
            _gain = DeviceSettings.MicrophoneGain > 0 ? DeviceSettings.MicrophoneGain : 1f;

            Text = "PISMO — Проверка микрофона";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(30, 31, 34);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 190);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var title = new Label
            {
                Text = "🎤 Говорите — вы слышите себя",
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                AutoSize = true, Location = new Point(18, 16)
            };
            _bar = new Panel { Location = new Point(18, 52), Size = new Size(404, 26), BackColor = Color.FromArgb(32, 34, 37) };
            _fill = new Panel { Location = new Point(0, 0), Size = new Size(0, 26), BackColor = Color.FromArgb(59, 165, 93) };
            _bar.Controls.Add(_fill);
            _st = new Label
            {
                Text = "Запуск…", ForeColor = Color.FromArgb(168, 170, 176),
                Font = new Font("Segoe UI", 8.5f), AutoSize = false,
                Location = new Point(18, 88), Size = new Size(404, 20)
            };
            var hint = new Label
            {
                Text = "Сила шумоподавления — в настройках; меняется здесь вживую.\nВключён шумодав — постучите по клавиатуре: в звонке он давится так же.",
                ForeColor = Color.FromArgb(114, 118, 125), Font = new Font("Segoe UI", 8f),
                AutoSize = false, Location = new Point(18, 116), Size = new Size(404, 46)
            };
            Controls.AddRange(new Control[] { title, _bar, _st, hint });

            _ui = new System.Windows.Forms.Timer { Interval = 33 };
            _ui.Tick += (s, e) =>
            {
                int w = (int)Math.Round(Math.Min(1f, _level * 3f) * _bar.Width);
                if (_fill.Width != w) _fill.Width = w;
                _fill.BackColor = _level * 3f > 0.85f ? Color.FromArgb(237, 66, 69)
                    : _level * 3f > 0.5f ? Color.FromArgb(250, 166, 26) : Color.FromArgb(59, 165, 93);
            };
            _ui.Start();

            Load += (s, e) => StartCapture();
        }

        private int FindMicIndex(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return 0;
            try
            {
                for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                {
                    var n = WaveInEvent.GetCapabilities(i).ProductName ?? "";
                    // Имена MME обрезаются до 31 символа — сверяем по вхождению.
                    if (n.Length > 0 && (label.Contains(n, StringComparison.OrdinalIgnoreCase)
                                         || n.Contains(label, StringComparison.OrdinalIgnoreCase)))
                        return i;
                }
            }
            catch { }
            return 0;
        }

        private void StartCapture()
        {
            StopCapture();
            try
            {
                _rn = _noise ? RnnoiseDenoiser.TryCreate() : null;
                _spectral = new SpectralDenoiser();
                _gate = new MicDenoiser(SR);

                _buf = new BufferedWaveProvider(new WaveFormat(SR, 16, 1))
                { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true };

                _out = new WaveOutEvent { DesiredLatency = 120 };
                _out.Init(_buf);
                _out.Play();

                _in = new WaveInEvent
                {
                    DeviceNumber = FindMicIndex(_micLabel),
                    WaveFormat = new WaveFormat(SR, 16, 1),
                    BufferMilliseconds = 20
                };
                _in.DataAvailable += OnData;
                _in.StartRecording();

                _st.Text = _noise
                    ? (_rn != null && _rn.IsReady ? "✓ Нативный шумодав активен (RNNoise + спектральный) — слышите себя"
                                                  : "✓ Спектральный шумодав активен — слышите себя")
                    : "Слышите себя (шумодав выключен)";
            }
            catch (Exception ex)
            {
                _st.Text = "Не удалось запустить тест: " + ex.Message;
            }
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            int len = e.BytesRecorded;
            if (len <= 0) return;
            var data = e.Buffer;

            // Шумодав — тот же тракт, что в звонке: RNNoise + спектральный, сила
            // спектрального = ползунок «Сила шумоподавления» (0..100 → Strength).
            int s = DeviceSettings.NoiseSuppressionStrength;
            if (_noise && s > 0)
            {
                float f = s / 100f;
                // ТОЛЬКО RNNoise (сила = сухой/мокрый микс) — тот же чистый тракт,
                // что в звонке. Никаких наслоений спектрального/гейта (давали «рацию»).
                var rn = _rn;
                if (rn != null && rn.IsReady) { rn.Mix = f; try { rn.Process(data, 0, len); } catch { } }
                else if (_spectral != null)   // fallback без RNNoise — мягкий спектральный
                {
                    _spectral.Strength = 0.4f + f * 1.4f;
                    _spectral.Floor    = 0.18f - f * 0.08f;
                    try { _spectral.Process(data, 0, len); } catch { }
                }
            }

            // Усиление + уровень (i16 LE, моно).
            float g = _gain;
            double sum = 0; int n = len & ~1;
            for (int i = 0; i < n; i += 2)
            {
                int v = (short)(data[i] | (data[i + 1] << 8));
                if (g != 1f) { v = (int)(v * g); if (v > short.MaxValue) v = short.MaxValue; else if (v < short.MinValue) v = short.MinValue; data[i] = (byte)(v & 0xFF); data[i + 1] = (byte)((v >> 8) & 0xFF); }
                float f = v / 32768f; sum += f * f;
            }
            _level = (float)Math.Sqrt(sum / Math.Max(1, n / 2));

            try { _buf?.AddSamples(data, 0, len); } catch { }
        }

        private void StopCapture()
        {
            try { if (_in != null) { _in.DataAvailable -= OnData; _in.StopRecording(); _in.Dispose(); } } catch { }
            _in = null;
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
            _buf = null;
            try { _rn?.Dispose(); } catch { }
            _rn = null; _spectral = null; _gate = null;
        }

        /// <summary>Живо применить усиление микрофона (без перезапуска).</summary>
        public void ApplyGain(float gain) { _gain = gain > 0 ? gain : 1f; }

        /// <summary>Сменить устройство/вкл-выкл шумодава — перезапуск захвата.
        /// (Сила шумодава читается из настроек на каждом буфере — перезапуск не нужен.)</summary>
        public void ApplyConfig(bool noise, string micLabel)
        {
            bool changed = noise != _noise || !string.Equals(micLabel ?? "", _micLabel ?? "", StringComparison.Ordinal);
            _noise = noise;
            _micLabel = micLabel ?? "";
            if (changed && IsHandleCreated && !IsDisposed)
                try { BeginInvoke(new Action(StartCapture)); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _ui.Stop(); _ui.Dispose(); } catch { }
            StopCapture();
            base.OnFormClosed(e);
        }
    }
}
