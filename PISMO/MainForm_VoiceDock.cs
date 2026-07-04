using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>Глобальное состояние кнопок футера (микрофон/наушники): действует
    /// на текущий звонок и автоматически применяется к каждому новому.</summary>
    public static class VoiceState
    {
        public static bool MicMuted;   // 🎤 выключен
        public static bool Deafened;   // 🎧 весь входящий звук заглушён
    }

    // «Голосовой док» внизу сайдбара (как в Discord): пока идёт звонок, над
    // карточкой профиля показывается скруглённая панель «Голосовая связь
    // подключена» (зелёная) с именем собеседника/группы и кнопкой завершения.
    // Здесь же — скругление углов (карточка футера, кнопки), чтобы уйти от
    // «топорных» прямых углов.
    public partial class MainForm : Form
    {
        private Panel _voiceDock;
        private Label _voiceTitle;
        private Label _voiceSub;
        private Button _voiceHangup;
        private Panel _voiceRadar;     // зелёный «радар» — клик показывает пинг
        private Button _voiceEq;       // «эквалайзер» — вкл/выкл шумодава
        private Label _pingChip;       // чип «NN ms»
        private System.Windows.Forms.Timer _pingChipTimer;
        private readonly ToolTip _voiceTip = new ToolTip();
        private Form _voiceDockCall;   // окно звонка, к которому привязан док (серверный голос);
                                       // null => используется _activeCall (личный/групповой)

        /// <summary>Окно звонка, к которому относится док.</summary>
        private Form DockCallWindow()
            => (_voiceDockCall != null && !_voiceDockCall.IsDisposed) ? _voiceDockCall : _activeCall;

        /// <summary>Показывает чип «NN ms» над радаром на 2 секунды.</summary>
        private void ShowPingChip()
        {
            int ms = (DockCallWindow() as CallForm)?.CurrentPingMs ?? 0;
            _pingChip.Text = ms > 0 ? $"{ms} ms" : "…";
            _pingChip.Visible = true;
            _pingChip.BringToFront();
            _pingChipTimer.Stop(); _pingChipTimer.Start();
        }

        /// <summary>Экземпляр главной формы — для показа дока из других окон (ServersForm).</summary>
        public static MainForm Current { get; private set; }

        private static readonly Color CardBack = Color.FromArgb(35, 36, 41);   // скруглённая карточка
        private static readonly Color FooterBack = Color.FromArgb(28, 29, 34); // фон полосы футера

        /// <summary>Скругляет контрол через Region; поддерживает ресайз.</summary>
        public static void RoundCorners(Control c, int radius)
        {
            void Apply()
            {
                if (c.Width <= 0 || c.Height <= 0) return;
                using var p = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius);
                c.Region = new Region(p);
            }
            Apply();
            c.Resize += (s, e) => Apply();
        }

        /// <summary>Создаёт голосовой док и карточку-футер; вызывать после BuildSidebarSearch.</summary>
        private void BuildVoiceDock()
        {
            Current = this;
            _voiceDock = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 64,
                BackColor = FooterBack,
                Visible = false
            };
            _voiceDock.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(6, 4, _voiceDock.Width - 12, _voiceDock.Height - 8);
                using var path = RoundedRect(rect, 10);
                using var br = new SolidBrush(CardBack);
                g.FillPath(br, path);
                // Индикатор-«радар» — отдельный кликабельный контрол (_voiceRadar).
            };

            _voiceTitle = new Label
            {
                Text = "Голосовая связь подключена",
                ForeColor = Color.FromArgb(59, 165, 93),
                BackColor = CardBack,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(30, 9),
                Size = new Size(140, 18),
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            _voiceSub = new Label
            {
                Text = "",
                ForeColor = Color.FromArgb(150, 152, 158),
                BackColor = CardBack,
                Font = new Font("Segoe UI", 8f),
                AutoSize = false,
                Location = new Point(30, 28),
                Size = new Size(140, 16),
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };

            _voiceHangup = new Button
            {
                Text = "☎",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Size = new Size(32, 32),
                Location = new Point(206, 15),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(237, 66, 69),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _voiceHangup.FlatAppearance.BorderSize = 0;
            RoundCorners(_voiceHangup, 16);
            new ToolTip().SetToolTip(_voiceHangup, "Завершить звонок");

            // Клик по панели/подписям — вернуться в окно звонка.
            void FocusCall(object s, EventArgs e)
            {
                try { var c = DockCallWindow(); if (c != null && !c.IsDisposed) c.Activate(); } catch { }
            }
            _voiceDock.Click += FocusCall;
            _voiceTitle.Click += FocusCall;
            _voiceSub.Click += FocusCall;
            _voiceHangup.Click += (s, e) =>
            {
                try { var c = DockCallWindow(); if (c != null && !c.IsDisposed) c.Close(); } catch { }
                HideVoiceDock();
            };

            // «Радар» — зелёный индикатор; клик/наведение показывает пинг (как в Discord).
            _voiceRadar = new Panel
            {
                Size = new Size(16, 16),
                Location = new Point(10, 10),
                BackColor = CardBack,
                Cursor = Cursors.Hand
            };
            _voiceRadar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var dot = new SolidBrush(Color.FromArgb(59, 165, 93));
                e.Graphics.FillEllipse(dot, 4, 4, 8, 8);
                using var arc = new Pen(Color.FromArgb(59, 165, 93), 1.6f);
                e.Graphics.DrawArc(arc, 1, 1, 14, 14, -60, 120);
            };
            _voiceRadar.Click += (s, e) => ShowPingChip();
            _voiceRadar.MouseEnter += (s, e) =>
            {
                int ms = (DockCallWindow() as CallForm)?.CurrentPingMs ?? 0;
                _voiceTip.SetToolTip(_voiceRadar, ms > 0 ? $"{ms} ms" : "Пинг измеряется…");
            };
            _voiceDock.Controls.Add(_voiceRadar);

            // Чип с пингом (появляется по клику на радар, гаснет через 2с).
            _pingChip = new Label
            {
                AutoSize = true,
                BackColor = Color.FromArgb(17, 18, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Padding = new Padding(8, 3, 8, 3),
                Location = new Point(8, 2),
                Visible = false
            };
            RoundCorners(_pingChip, 8);
            _voiceDock.Controls.Add(_pingChip);
            _pingChip.BringToFront();
            _pingChipTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pingChipTimer.Tick += (s, e) => { _pingChipTimer.Stop(); _pingChip.Visible = false; };

            // «Эквалайзер» — вкл/выкл шумодава (зелёный, когда включён).
            _voiceEq = new Button
            {
                Text = "🎚",
                Font = new Font("Segoe UI", 11f),
                Size = new Size(30, 32),
                Location = new Point(172, 15),
                FlatStyle = FlatStyle.Flat,
                BackColor = CardBack,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _voiceEq.FlatAppearance.BorderSize = 0;
            void PaintEq() => _voiceEq.ForeColor = DeviceSettings.NoiseSuppression
                ? Color.FromArgb(59, 165, 93) : Color.FromArgb(150, 152, 158);
            PaintEq();
            _voiceEq.Click += (s, e) =>
            {
                DeviceSettings.NoiseSuppression = !DeviceSettings.NoiseSuppression;
                try { DeviceSettings.Save(); } catch { }
                PaintEq();
                (DockCallWindow() as CallForm)?.SetNoiseSuppressionLive(DeviceSettings.NoiseSuppression);
                _voiceTip.SetToolTip(_voiceEq, DeviceSettings.NoiseSuppression ? "Шумоподавление: вкл" : "Шумоподавление: выкл");
            };
            _voiceTip.SetToolTip(_voiceEq, "Шумоподавление");
            _voiceDock.Controls.Add(_voiceEq);

            _voiceDock.Controls.Add(_voiceTitle);
            _voiceDock.Controls.Add(_voiceSub);
            _voiceDock.Controls.Add(_voiceHangup);
            pnlSidebar.Controls.Add(_voiceDock);

            // Z-порядок дока (обрабатывается от ВЫСШЕГО индекса к низшему):
            // шапка (верх) → поиск (верх) → футер (низ) → голосовой док (низ, НАД
            // футером) → список (Fill, остаток).
            try
            {
                pnlSidebar.Controls.SetChildIndex(pnlUserList, 0);
                pnlSidebar.Controls.SetChildIndex(_voiceDock, 1);
                pnlSidebar.Controls.SetChildIndex(pnlSidebarFooter, 2);
                if (_convSearchHost != null) pnlSidebar.Controls.SetChildIndex(_convSearchHost, 3);
                pnlSidebar.Controls.SetChildIndex(pnlSidebarHeader, 4);
            }
            catch { }

            // Футер — скруглённая карточка профиля (как нижняя плашка в Discord).
            try
            {
                pnlSidebarFooter.Paint += (s, e) =>
                {
                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var rect = new Rectangle(4, 4, pnlSidebarFooter.Width - 8, pnlSidebarFooter.Height - 8);
                    using var path = RoundedRect(rect, 10);
                    using var br = new SolidBrush(CardBack);
                    g.FillPath(br, path);
                };
                lblCurrentUser.BackColor = Color.Transparent;
                pnlSidebarFooter.Padding = new Padding(10, 8, 8, 8);
            }
            catch { }

            BuildFooterVoiceButtons();
        }

        /// <summary>Кнопки в футере (как в Discord): 🎤 мьют + ▾ выбор микрофона,
        /// 🎧 заглушить всё + ▾ выбор вывода. Работают и вне звонка (состояние
        /// применится к следующему), и на лету в текущем.</summary>
        private void BuildFooterVoiceButtons()
        {
            // Футер двухэтажный: ВЕРХНИЙ ряд — кнопки (🎤▾ 🎧▾ ⚙), НИЖНИЙ — аватар
            // и имя на всю ширину (раньше кнопки в одну строку с именем его
            // перекрывали, текст было «не до конца видно»).
            pnlSidebarFooter.Height = 88;
            try
            {
                pnlMyAvatar.Dock = DockStyle.None;
                pnlMyAvatar.Size = new Size(36, 36);
                pnlMyAvatar.Location = new Point(12, 44);
                lblCurrentUser.Dock = DockStyle.None;
                lblCurrentUser.AutoEllipsis = true;
                lblCurrentUser.Location = new Point(54, 48);
                lblCurrentUser.Size = new Size(pnlSidebarFooter.Width - 62, 28);
                lblCurrentUser.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
                btnSettings.Dock = DockStyle.None;
                btnSettings.Size = new Size(30, 30);
                btnSettings.Location = new Point(pnlSidebarFooter.Width - 40, 8);
                btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            }
            catch { }

            Button Mk(string text, int w, int x, float fontSize = 10.5f)
            {
                var b = new Button
                {
                    Text = text,
                    Font = new Font("Segoe UI", fontSize),
                    Size = new Size(w, 30),
                    Location = new Point(x, 8),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(185, 187, 190),
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 52, 58);
                return b;
            }

            // Справа налево: ⚙(W-40) ▾(W-58) 🎧(W-90) ▾(W-108) 🎤(W-140)
            int W = pnlSidebarFooter.Width;
            var btnSpkArrow = Mk("▾", 16, W - 58, 8f);
            var btnSpk = Mk("🎧", 30, W - 90);
            var btnMicArrow = Mk("▾", 16, W - 108, 8f);
            var btnMic = Mk("🎤", 30, W - 140);

            void PaintStates()
            {
                btnMic.Text = VoiceState.MicMuted ? "🔇" : "🎤";
                btnMic.ForeColor = VoiceState.MicMuted ? Color.FromArgb(237, 66, 69) : Color.FromArgb(185, 187, 190);
                btnSpk.Text = VoiceState.Deafened ? "🔕" : "🎧";
                btnSpk.ForeColor = VoiceState.Deafened ? Color.FromArgb(237, 66, 69) : Color.FromArgb(185, 187, 190);
                _voiceTip.SetToolTip(btnMic, VoiceState.MicMuted ? "Включить микрофон" : "Выключить микрофон");
                _voiceTip.SetToolTip(btnSpk, VoiceState.Deafened ? "Включить звук" : "Отключить звук");
            }
            PaintStates();
            _voiceTip.SetToolTip(btnMicArrow, "Устройство ввода");
            _voiceTip.SetToolTip(btnSpkArrow, "Устройство вывода");

            btnMic.Click += (s, e) =>
            {
                VoiceState.MicMuted = !VoiceState.MicMuted;
                (DockCallWindow() as CallForm)?.SetMicMutedPublic(VoiceState.MicMuted);
                PaintStates();
            };
            btnSpk.Click += (s, e) =>
            {
                VoiceState.Deafened = !VoiceState.Deafened;
                (DockCallWindow() as CallForm)?.SetAllMutedPublic(VoiceState.Deafened);
                PaintStates();
            };

            // ▾ микрофон — список устройств ввода (NAudio), галочка на выбранном.
            btnMicArrow.Click += (s, e) =>
            {
                var menu = new ContextMenuStrip();
                try
                {
                    for (int i = 0; i < WaveInEvent.DeviceCount; i++)
                    {
                        string nm = WaveInEvent.GetCapabilities(i).ProductName;
                        int idx = i;
                        var item = new ToolStripMenuItem(nm)
                        { Checked = nm == DeviceSettings.MicrophoneName || idx == DeviceSettings.MicrophoneIndex };
                        item.Click += (s2, e2) =>
                        {
                            DeviceSettings.MicrophoneIndex = idx;
                            DeviceSettings.MicrophoneName = nm;
                            try { DeviceSettings.Save(); } catch { }
                            (DockCallWindow() as CallForm)?.SetInputDeviceLive(nm);
                        };
                        menu.Items.Add(item);
                    }
                }
                catch { }
                if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("Микрофоны не найдены") { Enabled = false });
                menu.Show(btnMicArrow, new Point(0, -menu.Items.Count * 24));
            };

            // ▾ вывод — список устройств воспроизведения (NAudio).
            btnSpkArrow.Click += (s, e) =>
            {
                var menu = new ContextMenuStrip();
                try
                {
                    var def = new ToolStripMenuItem("Системное по умолчанию")
                    { Checked = string.IsNullOrWhiteSpace(DeviceSettings.SpeakerName) };
                    def.Click += (s2, e2) =>
                    {
                        DeviceSettings.SpeakerName = "";
                        try { DeviceSettings.Save(); } catch { }
                    };
                    menu.Items.Add(def);
                    for (int i = 0; i < WaveOut.DeviceCount; i++)
                    {
                        string nm = WaveOut.GetCapabilities(i).ProductName;
                        var item = new ToolStripMenuItem(nm) { Checked = nm == DeviceSettings.SpeakerName };
                        item.Click += (s2, e2) =>
                        {
                            DeviceSettings.SpeakerName = nm;
                            try { DeviceSettings.Save(); } catch { }
                            (DockCallWindow() as CallForm)?.SetOutputDeviceLive(nm);
                        };
                        menu.Items.Add(item);
                    }
                }
                catch { }
                menu.Show(btnSpkArrow, new Point(0, -menu.Items.Count * 24));
            };

            pnlSidebarFooter.Controls.Add(btnMic);
            pnlSidebarFooter.Controls.Add(btnMicArrow);
            pnlSidebarFooter.Controls.Add(btnSpk);
            pnlSidebarFooter.Controls.Add(btnSpkArrow);
        }

        /// <summary>Показать «Голосовая связь подключена» (subtitle — с кем/где звонок;
        /// call — окно звонка, если это не _activeCall, например серверный голос).</summary>
        private void ShowVoiceDock(string subtitle, Form call = null)
        {
            if (_voiceDock == null) return;
            _voiceDockCall = call;
            _voiceSub.Text = subtitle ?? "";
            _voiceDock.Visible = true;
            _voiceDock.Invalidate();
        }

        /// <summary>Спрятать голосовой док (звонок завершён).</summary>
        private void HideVoiceDock()
        {
            _voiceDockCall = null;
            if (_voiceDock != null) _voiceDock.Visible = false;
        }

        /// <summary>Показ дока из другого окна (голосовой канал сервера).</summary>
        public void NotifyVoiceStarted(string subtitle, Form call)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(() => ShowVoiceDock(subtitle, call))); }
                else ShowVoiceDock(subtitle, call);
            }
            catch { }
        }

        /// <summary>Скрытие дока из другого окна.</summary>
        public void NotifyVoiceEnded()
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action(HideVoiceDock)); }
                else HideVoiceDock();
            }
            catch { }
        }
    }
}
