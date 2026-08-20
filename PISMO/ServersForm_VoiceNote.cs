using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Голосовые в каналах серверов: запись кнопкой и полоса предпрослушивания.
    ///
    /// Один в один то же, что в мессенджере (MainForm_VoiceNote), и по той же
    /// причине: держать кнопку мышью неудобно, а отпускание сразу отправляло
    /// записанное — ни послушать, ни передумать. Теперь клик начинает запись,
    /// второй клик её заканчивает, а голосовое ждёт в полосе над полем ввода,
    /// пока его не отправят обычной кнопкой отправки.
    ///
    /// Полоса живёт в том же нижнем контейнере, что полоска ответа и превью
    /// вложения, и так же учитывается в UpdateBottomHeight.
    /// </summary>
    public partial class ServersForm
    {
        private bool _chVoiceRecording;
        private System.Windows.Forms.Timer _chVoiceTimer;
        private int _chVoiceSeconds;

        /// <summary>Записанное, но ещё не отправленное голосовое (WAV).</summary>
        private byte[] _chPendingVoice;
        private int _chPendingVoiceSeconds;

        private Panel _chVoiceBar;
        private Label _chVoiceLbl;
        private Button _btnChVoicePlay;
        private Button _btnChVoiceRedo;
        private Button _btnChVoiceDrop;

        private WaveOutEvent _chVoiceOut;
        private WaveFileReader _chVoiceReader;

        /// <summary>Ползунок перемотки — см. пояснения в MainForm_VoiceNote.</summary>
        private FlatSlider _chVoiceSeek;
        private System.Windows.Forms.Timer _chVoicePlayTimer;
        private bool _chVoiceSeekSuppress;
        private int _chVoiceSeekSeconds;

        /// <summary>Тот же потолок, что в мессенджере и на телефоне.</summary>
        private const int ChVoiceMaxSeconds = 180;

        private void BuildChannelVoiceBar()
        {
            _chVoiceBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(47, 49, 54),
                Visible = false
            };

            _chVoiceLbl = new Label
            {
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(88, 101, 242),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            _btnChVoiceDrop = MakeChVoiceButton("✕", "Удалить запись");
            _btnChVoiceDrop.Click += (s, e) => DropChannelPendingVoice();

            _btnChVoiceRedo = MakeChVoiceButton("⟳", "Перезаписать: старое стирается, запись начинается сразу");
            _btnChVoiceRedo.Click += (s, e) => { DropChannelPendingVoice(); StartChannelVoiceRecording(); };

            _btnChVoicePlay = MakeChVoiceButton("▶", "Прослушать");
            _btnChVoicePlay.Click += (s, e) => ToggleChannelVoicePlayback();

            _chVoiceSeek = new FlatSlider
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Visible = false
            };
            _chVoiceSeek.ValueChanged += (s, e) =>
            {
                if (_chVoiceSeekSuppress) return;
                _chVoiceSeekSeconds = _chVoiceSeek.Value;
                try
                {
                    if (_chVoiceReader != null)
                        _chVoiceReader.CurrentTime = TimeSpan.FromSeconds(_chVoiceSeekSeconds);
                }
                catch { }
                UpdateChVoiceLabel();
            };

            _chVoiceBar.Controls.Add(_chVoiceLbl);
            _chVoiceBar.Controls.Add(_btnChVoiceDrop);
            _chVoiceBar.Controls.Add(_btnChVoiceRedo);
            _chVoiceBar.Controls.Add(_btnChVoicePlay);
            // Последним — чтобы занять нижнюю кромку (см. MainForm_VoiceNote).
            _chVoiceBar.Controls.Add(_chVoiceSeek);
        }

        private Button MakeChVoiceButton(string text, string tip)
        {
            var b = new Button
            {
                Text = text,
                Dock = DockStyle.Right,
                Width = 40,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 11f),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            try { new ToolTip().SetToolTip(b, tip); } catch { }
            return b;
        }

        // ── Запись ──────────────────────────────────────────────────────

        private void ToggleChannelVoiceRecording()
        {
            if (_chVoiceRecording) StopChannelVoiceRecording(keep: true);
            else StartChannelVoiceRecording();
        }

        private void StartChannelVoiceRecording()
        {
            if (_chVoiceRecording) return;
            if (_channelId <= 0) return;

            StopChannelVoicePlayback();

            try
            {
                _chAudioStream = new MemoryStream();
                _chWaveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };
                if (DeviceSettings.MicrophoneIndex >= 0 && DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                    _chWaveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;
                _chWaveWriter = new WaveFileWriter(_chAudioStream, _chWaveIn.WaveFormat);

                float gain = DeviceSettings.MicrophoneGain;
                _chWaveIn.DataAvailable += (s, ev) =>
                {
                    try
                    {
                        // Усиление микрофона учитываем и здесь: раньше в каналах
                        // оно молча игнорировалось, и голосовые оттуда выходили
                        // тише, чем из личных чатов.
                        if (Math.Abs(gain - 1f) > 0.01f)
                            _chWaveWriter?.Write(MainForm.ApplyGain(ev.Buffer, ev.BytesRecorded, gain), 0, ev.BytesRecorded);
                        else
                            _chWaveWriter?.Write(ev.Buffer, 0, ev.BytesRecorded);
                    }
                    catch { }
                };

                _chWaveIn.StartRecording();
                _chVoiceRecording = true;
                _chVoiceSeconds = 0;

                if (_btnChVoice != null) { _btnChVoice.ForeColor = Color.FromArgb(240, 71, 71); _btnChVoice.Text = "⏹"; }

                _chVoiceTimer ??= MakeChVoiceTimer();
                _chVoiceTimer.Start();
                ShowChannelVoiceBar("● Идёт запись… 0:00 — нажмите кнопку микрофона, чтобы остановить", recording: true);
            }
            catch (Exception ex)
            {
                _chVoiceRecording = false;
                MessageBox.Show("Нет доступа к микрофону: " + ex.Message, "PISMO");
            }
        }

        private System.Windows.Forms.Timer MakeChVoiceTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += (s, e) =>
            {
                _chVoiceSeconds++;
                if (_chVoiceLbl != null && _chVoiceRecording)
                    _chVoiceLbl.Text = $"● Идёт запись… {ChMmss(_chVoiceSeconds)} — нажмите кнопку микрофона, чтобы остановить";
                if (_chVoiceSeconds >= ChVoiceMaxSeconds) StopChannelVoiceRecording(keep: true);
            };
            return t;
        }

        private void StopChannelVoiceRecording(bool keep)
        {
            if (!_chVoiceRecording) return;
            _chVoiceRecording = false;
            _chVoiceTimer?.Stop();

            byte[] bytes = null;
            try
            {
                _chWaveIn?.StopRecording();
                _chWaveIn?.Dispose(); _chWaveIn = null;
                _chWaveWriter?.Flush();
                bytes = _chAudioStream?.ToArray();
                _chWaveWriter?.Dispose(); _chWaveWriter = null;
                _chAudioStream?.Dispose(); _chAudioStream = null;
            }
            catch { }

            if (_btnChVoice != null) { _btnChVoice.ForeColor = Color.FromArgb(220, 221, 222); _btnChVoice.Text = "🎤"; }

            if (!keep || bytes == null || bytes.Length <= 4000)
            {
                HideChannelVoiceBar();
                return;
            }

            _chPendingVoice = bytes;
            _chPendingVoiceSeconds = _chVoiceSeconds;
            ShowChannelVoiceBar($"🎤 Голосовое {ChMmss(_chPendingVoiceSeconds)} — «Отправить», чтобы отправить", recording: false);
        }

        private static string ChMmss(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

        private void UpdateChVoiceLabel()
        {
            if (_chVoiceLbl == null || _chPendingVoice == null) return;
            _chVoiceLbl.Text = _chVoiceOut != null
                ? $"🎤 Голосовое {ChMmss(_chVoiceSeekSeconds)} / {ChMmss(_chPendingVoiceSeconds)}"
                : $"🎤 Голосовое {ChMmss(_chPendingVoiceSeconds)} — «Отправить», чтобы отправить";
        }

        // ── Полоса ──────────────────────────────────────────────────────

        private void ShowChannelVoiceBar(string text, bool recording)
        {
            if (_chVoiceBar == null) return;
            _chVoiceLbl.Text = text;
            _chVoiceLbl.ForeColor = recording ? Color.FromArgb(240, 71, 71) : Color.FromArgb(88, 101, 242);
            _btnChVoicePlay.Visible = !recording;
            _btnChVoiceRedo.Visible = !recording;
            _chVoiceSeek.Visible = !recording;
            if (!recording)
            {
                _chVoiceSeekSuppress = true;
                _chVoiceSeek.Maximum = Math.Max(1, _chPendingVoiceSeconds);
                _chVoiceSeek.Value = 0;
                _chVoiceSeekSuppress = false;
                _chVoiceSeekSeconds = 0;
            }
            _chVoiceBar.Height = recording ? 42 : 62;
            _chVoiceBar.Visible = true;
            UpdateBottomHeight();
        }

        private void HideChannelVoiceBar()
        {
            if (_chVoiceBar == null) return;
            _chVoiceBar.Visible = false;
            _chVoiceBar.Height = 0;
            UpdateBottomHeight();
        }

        private void DropChannelPendingVoice()
        {
            StopChannelVoicePlayback();
            _chPendingVoice = null;
            _chPendingVoiceSeconds = 0;
            HideChannelVoiceBar();
        }

        /// <summary>Забрать записанное для отправки; null — записи нет.</summary>
        private byte[] TakeChannelPendingVoice()
        {
            var bytes = _chPendingVoice;
            if (bytes == null) return null;
            DropChannelPendingVoice();
            return bytes;
        }

        /// <summary>Смена канала: недописанное и неотправленное не переносим.</summary>
        private void ResetChannelVoiceNote()
        {
            if (_chVoiceRecording) StopChannelVoiceRecording(keep: false);
            DropChannelPendingVoice();
        }

        // ── Прослушивание ───────────────────────────────────────────────

        private void ToggleChannelVoicePlayback()
        {
            if (_chVoiceOut != null) { StopChannelVoicePlayback(); return; }
            if (_chPendingVoice == null) return;

            try
            {
                _chVoiceReader = new WaveFileReader(new MemoryStream(_chPendingVoice));
                _chVoiceOut = new WaveOutEvent();
                _chVoiceOut.Init(_chVoiceReader);
                _chVoiceOut.PlaybackStopped += (s, e) =>
                {
                    try { BeginInvoke(new Action(StopChannelVoicePlayback)); } catch { }
                };
                try { _chVoiceReader.CurrentTime = TimeSpan.FromSeconds(_chVoiceSeekSeconds); } catch { }
                _chVoiceOut.Play();
                _btnChVoicePlay.Text = "⏸";
                _chVoicePlayTimer ??= MakeChVoicePlayTimer();
                _chVoicePlayTimer.Start();
            }
            catch { StopChannelVoicePlayback(); }
        }

        private System.Windows.Forms.Timer MakeChVoicePlayTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 200 };
            t.Tick += (s, e) =>
            {
                if (_chVoiceReader == null || _chVoiceOut == null) return;
                try
                {
                    _chVoiceSeekSeconds = (int)_chVoiceReader.CurrentTime.TotalSeconds;
                    _chVoiceSeekSuppress = true;
                    _chVoiceSeek.Value = Math.Clamp(_chVoiceSeekSeconds, _chVoiceSeek.Minimum, _chVoiceSeek.Maximum);
                    _chVoiceSeekSuppress = false;
                    UpdateChVoiceLabel();
                }
                catch { }
            };
            return t;
        }

        private void StopChannelVoicePlayback()
        {
            _chVoicePlayTimer?.Stop();
            try { _chVoiceOut?.Stop(); } catch { }
            try { _chVoiceOut?.Dispose(); } catch { }
            try { _chVoiceReader?.Dispose(); } catch { }
            _chVoiceOut = null;
            _chVoiceReader = null;
            if (_btnChVoicePlay != null) _btnChVoicePlay.Text = "▶";
            if (_chPendingVoiceSeconds > 0 && _chVoiceSeekSeconds >= _chPendingVoiceSeconds)
            {
                _chVoiceSeekSeconds = 0;
                if (_chVoiceSeek != null)
                {
                    _chVoiceSeekSuppress = true;
                    _chVoiceSeek.Value = 0;
                    _chVoiceSeekSuppress = false;
                }
            }
            UpdateChVoiceLabel();
        }
    }
}
