using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Голосовые сообщения: запись кнопкой и полоса предпрослушивания.
    ///
    /// БЫЛО: кнопку надо было ДЕРЖАТЬ. Отпустил — голосовое ушло собеседнику,
    /// причём сразу и без вариантов: ни услышать себя, ни передумать, ни
    /// перезаписать. Держать кнопку три минуты мышью — отдельное удовольствие,
    /// а любое случайное отпускание отправляло обрывок.
    ///
    /// СТАЛО: клик — начали писать, клик — закончили. Записанное никуда не
    /// уходит, а появляется полосой над строкой ввода: послушать, перезаписать
    /// (старое стирается и запись начинается тут же) или выбросить. Отправляется
    /// оно обычной кнопкой «Отправить», вместе с текстом, если он набран.
    ///
    /// Полоса вставляется тем же приёмом, что панель ответа (BuildReplyBar):
    /// Dock=Bottom плюс SetChildIndex на место pnlInputBar — так она встаёт
    /// НАД строкой ввода, а не под ней.
    /// </summary>
    public partial class MainForm
    {
        // ── Запись ──────────────────────────────────────────────────────
        private bool _voiceRecording;
        private System.Windows.Forms.Timer _voiceTimer;
        private int _voiceSeconds;

        /// <summary>Записанное, но ещё не отправленное голосовое (WAV).</summary>
        private byte[] _pendingVoice;

        /// <summary>Длительность записанного, для подписи на полосе.</summary>
        private int _pendingVoiceSeconds;

        // ── Полоса предпрослушивания ────────────────────────────────────
        private Panel _pnlVoiceBar;
        private Label _lblVoiceInfo;
        private Button _btnVoicePlay;
        private Button _btnVoiceRedo;
        private Button _btnVoiceDrop;

        // ── Проигрывание ────────────────────────────────────────────────
        private WaveOutEvent _voiceOut;
        private WaveFileReader _voiceReader;

        /// <summary>Ползунок перемотки записанного.</summary>
        private FlatSlider _voiceSeek;

        /// <summary>Тик проигрывания: двигает ползунок и подпись.</summary>
        private System.Windows.Forms.Timer _voicePlayTimer;

        /// <summary>
        /// Ползунок двигаем и мы сами, и человек мышью. Без этого флага наше
        /// же обновление позиции читалось бы как перемотка, и звук топтался
        /// бы на месте.
        /// </summary>
        private bool _voiceSeekSuppress;

        /// <summary>Куда перемотали до нажатия «играть», в секундах.</summary>
        private int _voiceSeekSeconds;

        /// <summary>
        /// Три минуты — тот же потолок, что на телефоне. Голосовое целиком
        /// лежит в памяти и уходит в базу одним BLOB'ом, поэтому забытая
        /// запись без верхней границы раздувает и то, и другое.
        /// </summary>
        private const int VoiceMaxSeconds = 180;

        private void BuildVoiceBar()
        {
            _pnlVoiceBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                BackColor = Color.FromArgb(47, 49, 54),
                Visible = false
            };

            _lblVoiceInfo = new Label
            {
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(88, 101, 242),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            };

            _btnVoiceDrop = MakeVoiceButton("✕", "Удалить запись");
            _btnVoiceDrop.Click += (s, e) => DropPendingVoice();

            _btnVoiceRedo = MakeVoiceButton("⟳", "Перезаписать: старое стирается, запись начинается сразу");
            _btnVoiceRedo.Click += (s, e) =>
            {
                DropPendingVoice();
                StartVoiceRecording();
            };

            _btnVoicePlay = MakeVoiceButton("▶", "Прослушать");
            _btnVoicePlay.Click += (s, e) => TogglePendingVoicePlayback();

            _voiceSeek = new FlatSlider
            {
                Dock = DockStyle.Bottom,
                Height = 20,
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Visible = false
            };
            _voiceSeek.ValueChanged += (s, e) =>
            {
                if (_voiceSeekSuppress) return;
                _voiceSeekSeconds = _voiceSeek.Value;
                // Играет — перематываем прямо сейчас; не играет — запомнили,
                // и «играть» начнёт отсюда.
                try
                {
                    if (_voiceReader != null)
                        _voiceReader.CurrentTime = TimeSpan.FromSeconds(_voiceSeekSeconds);
                }
                catch { }
                UpdateVoiceLabel();
            };

            _pnlVoiceBar.Controls.Add(_lblVoiceInfo);
            // Порядок добавления задаёт порядок справа налево у Dock=Right.
            _pnlVoiceBar.Controls.Add(_btnVoiceDrop);
            _pnlVoiceBar.Controls.Add(_btnVoiceRedo);
            _pnlVoiceBar.Controls.Add(_btnVoicePlay);
            // Ползунок добавляем ПОСЛЕДНИМ: WinForms раскладывает пристыкованное
            // от большего индекса к меньшему, поэтому именно последний займёт
            // нижнюю кромку, а строка с подписью и кнопками — всё, что выше.
            _pnlVoiceBar.Controls.Add(_voiceSeek);

            pnlMain.Controls.Add(_pnlVoiceBar);
            pnlMain.Controls.SetChildIndex(_pnlVoiceBar, pnlMain.Controls.IndexOf(pnlInputBar));
        }

        private Button MakeVoiceButton(string text, string tip)
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

        // ════════════════════════════════════════════════════════════════
        //  ЗАПИСЬ
        // ════════════════════════════════════════════════════════════════

        /// <summary>Одно нажатие кнопки микрофона: начать или закончить запись.</summary>
        private void ToggleVoiceRecording()
        {
            if (_voiceRecording) StopVoiceRecording(keep: true);
            else StartVoiceRecording();
        }

        private void StartVoiceRecording()
        {
            if (_voiceRecording) return;
            if (_currentChatPartnerId < 0 && _currentGroupId < 0)
            {
                MessageBox.Show("Сначала выберите собеседника.");
                return;
            }

            StopPendingVoicePlayback();

            try
            {
                _audioStream = new MemoryStream();
                _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 1) };

                if (DeviceSettings.MicrophoneIndex >= 0 &&
                    DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                {
                    _waveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;
                }

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
                _voiceRecording = true;
                _voiceSeconds = 0;

                btnVoice.ForeColor = Color.FromArgb(240, 71, 71);
                btnVoice.Text = "⏹";

                _voiceTimer ??= MakeVoiceTimer();
                _voiceTimer.Start();
                ShowVoiceBar("● Идёт запись… 0:00 — нажмите кнопку микрофона, чтобы остановить", recording: true);
            }
            catch (Exception ex)
            {
                _voiceRecording = false;
                MessageBox.Show("Нет доступа к микрофону: " + ex.Message);
            }
        }

        private System.Windows.Forms.Timer MakeVoiceTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += (s, e) =>
            {
                _voiceSeconds++;
                if (_lblVoiceInfo != null && _voiceRecording)
                    _lblVoiceInfo.Text = $"● Идёт запись… {Mmss(_voiceSeconds)} — нажмите кнопку микрофона, чтобы остановить";
                // Потолок: дальше останавливаем сами, запись остаётся в полосе.
                if (_voiceSeconds >= VoiceMaxSeconds) StopVoiceRecording(keep: true);
            };
            return t;
        }

        /// <summary>
        /// [keep] = true — записанное остаётся в полосе; false — выбрасываем
        /// (уход из чата, закрытие окна).
        /// </summary>
        private void StopVoiceRecording(bool keep)
        {
            if (!_voiceRecording) return;
            _voiceRecording = false;
            _voiceTimer?.Stop();

            byte[] bytes = null;
            try
            {
                _waveIn?.StopRecording();
                _waveIn?.Dispose();
                _waveIn = null;

                _waveWriter?.Flush();
                bytes = _audioStream?.ToArray();

                _waveWriter?.Dispose();
                _waveWriter = null;
                _audioStream?.Dispose();
                _audioStream = null;
            }
            catch { }

            btnVoice.ForeColor = Color.FromArgb(142, 146, 151);
            btnVoice.Text = "🎤";

            // 4000 байт ≈ 0,1 секунды: случайный клик по кнопке не должен
            // оставлять «голосовое» из щелчка.
            if (!keep || bytes == null || bytes.Length <= 4000)
            {
                HideVoiceBar();
                return;
            }

            _pendingVoice = bytes;
            _pendingVoiceSeconds = _voiceSeconds;
            ShowVoiceBar($"🎤 Голосовое {Mmss(_pendingVoiceSeconds)} — «Отправить», чтобы отправить", recording: false);
        }

        private static string Mmss(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

        /// <summary>Подпись «сколько прошло из скольких» во время прослушивания.</summary>
        private void UpdateVoiceLabel()
        {
            if (_lblVoiceInfo == null || _pendingVoice == null) return;
            _lblVoiceInfo.Text = _voiceOut != null
                ? $"🎤 Голосовое {Mmss(_voiceSeekSeconds)} / {Mmss(_pendingVoiceSeconds)}"
                : $"🎤 Голосовое {Mmss(_pendingVoiceSeconds)} — «Отправить», чтобы отправить";
        }

        // ════════════════════════════════════════════════════════════════
        //  ПОЛОСА
        // ════════════════════════════════════════════════════════════════

        private void ShowVoiceBar(string text, bool recording)
        {
            if (_pnlVoiceBar == null) return;
            _lblVoiceInfo.Text = text;
            _lblVoiceInfo.ForeColor = recording
                ? Color.FromArgb(240, 71, 71)
                : Color.FromArgb(88, 101, 242);
            // Во время записи слушать и перезаписывать нечего.
            _btnVoicePlay.Visible = !recording;
            _btnVoiceRedo.Visible = !recording;
            // Во время записи перематывать нечего.
            _voiceSeek.Visible = !recording;
            if (!recording)
            {
                _voiceSeekSuppress = true;
                _voiceSeek.Maximum = Math.Max(1, _pendingVoiceSeconds);
                _voiceSeek.Value = 0;
                _voiceSeekSuppress = false;
                _voiceSeekSeconds = 0;
            }
            _pnlVoiceBar.Height = recording ? 42 : 62;
            _pnlVoiceBar.Visible = true;
        }

        private void HideVoiceBar()
        {
            if (_pnlVoiceBar != null) _pnlVoiceBar.Visible = false;
        }

        /// <summary>Выбросить записанное (кнопка ✕ и перезапись).</summary>
        private void DropPendingVoice()
        {
            StopPendingVoicePlayback();
            _pendingVoice = null;
            _pendingVoiceSeconds = 0;
            HideVoiceBar();
        }

        /// <summary>
        /// Забрать записанное для отправки. Возвращает null, если ничего нет.
        /// Полоса при этом гаснет — сообщение уже уходит.
        /// </summary>
        private byte[] TakePendingVoice()
        {
            var bytes = _pendingVoice;
            if (bytes == null) return null;
            DropPendingVoice();
            return bytes;
        }

        /// <summary>
        /// Прибраться при уходе из чата и при закрытии окна: запись не должна
        /// продолжаться в никуда, а чужое голосовое — всплыть в другом чате.
        /// </summary>
        private void ResetVoiceNote()
        {
            if (_voiceRecording) StopVoiceRecording(keep: false);
            DropPendingVoice();
        }

        // ════════════════════════════════════════════════════════════════
        //  ПРОСЛУШИВАНИЕ
        // ════════════════════════════════════════════════════════════════

        private void TogglePendingVoicePlayback()
        {
            if (_voiceOut != null) { StopPendingVoicePlayback(); return; }
            if (_pendingVoice == null) return;

            try
            {
                _voiceReader = new WaveFileReader(new MemoryStream(_pendingVoice));
                // Устройство вывода не выбираем — как в InlineAudioPlayer,
                // которым слушают уже отправленные голосовые: пусть звучит там
                // же, где и они.
                _voiceOut = new WaveOutEvent();
                _voiceOut.Init(_voiceReader);
                _voiceOut.PlaybackStopped += (s, e) =>
                {
                    // Событие приходит не из UI-потока.
                    try { BeginInvoke(new Action(StopPendingVoicePlayback)); } catch { }
                };
                try { _voiceReader.CurrentTime = TimeSpan.FromSeconds(_voiceSeekSeconds); } catch { }
                _voiceOut.Play();
                _btnVoicePlay.Text = "⏸";

                _voicePlayTimer ??= MakeVoicePlayTimer();
                _voicePlayTimer.Start();
            }
            catch
            {
                StopPendingVoicePlayback();
            }
        }

        private System.Windows.Forms.Timer MakeVoicePlayTimer()
        {
            // Пять раз в секунду: чаще ползунок дрожит, реже — заметно отстаёт
            // от звука.
            var t = new System.Windows.Forms.Timer { Interval = 200 };
            t.Tick += (s, e) =>
            {
                if (_voiceReader == null || _voiceOut == null) return;
                try
                {
                    _voiceSeekSeconds = (int)_voiceReader.CurrentTime.TotalSeconds;
                    _voiceSeekSuppress = true;
                    _voiceSeek.Value = Math.Clamp(_voiceSeekSeconds, _voiceSeek.Minimum, _voiceSeek.Maximum);
                    _voiceSeekSuppress = false;
                    UpdateVoiceLabel();
                }
                catch { }
            };
            return t;
        }

        private void StopPendingVoicePlayback()
        {
            _voicePlayTimer?.Stop();
            try { _voiceOut?.Stop(); } catch { }
            try { _voiceOut?.Dispose(); } catch { }
            try { _voiceReader?.Dispose(); } catch { }
            _voiceOut = null;
            _voiceReader = null;
            if (_btnVoicePlay != null) _btnVoicePlay.Text = "▶";
            // Доиграло до конца — ползунок к началу, чтобы следующее нажатие
            // не упиралось в хвост.
            if (_pendingVoiceSeconds > 0 && _voiceSeekSeconds >= _pendingVoiceSeconds)
            {
                _voiceSeekSeconds = 0;
                if (_voiceSeek != null)
                {
                    _voiceSeekSuppress = true;
                    _voiceSeek.Value = 0;
                    _voiceSeekSuppress = false;
                }
            }
            UpdateVoiceLabel();
        }
    }
}
