using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PISMO
{
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
        private Form _voiceDockCall;   // окно звонка, к которому привязан док (серверный голос);
                                       // null => используется _activeCall (личный/групповой)

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
                // Зелёный «радар»-индикатор слева (как в Discord).
                using var dot = new SolidBrush(Color.FromArgb(59, 165, 93));
                g.FillEllipse(dot, 16, 14, 8, 8);
            };

            _voiceTitle = new Label
            {
                Text = "Голосовая связь подключена",
                ForeColor = Color.FromArgb(59, 165, 93),
                BackColor = CardBack,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(30, 9),
                Size = new Size(172, 18),
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
                Size = new Size(172, 16),
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
            Form DockCall() => (_voiceDockCall != null && !_voiceDockCall.IsDisposed) ? _voiceDockCall : _activeCall;
            void FocusCall(object s, EventArgs e)
            {
                try { var c = DockCall(); if (c != null && !c.IsDisposed) c.Activate(); } catch { }
            }
            _voiceDock.Click += FocusCall;
            _voiceTitle.Click += FocusCall;
            _voiceSub.Click += FocusCall;
            _voiceHangup.Click += (s, e) =>
            {
                try { var c = DockCall(); if (c != null && !c.IsDisposed) c.Close(); } catch { }
                HideVoiceDock();
            };

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
