using System;
using System.Drawing;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    /// <summary>Небольшое окно уведомления о входящем звонке с кнопками Принять/Отклонить.</summary>
    public class IncomingCallForm : Form
    {
        public bool Accepted { get; private set; } = false;

        private readonly System.Windows.Forms.Timer _ringTimer;
        private readonly System.Windows.Forms.Timer _checkTimer;
        private int _blinkState = 0;
        private bool _checkBusy;   // фоновая проверка статуса уже идёт
        private readonly int _sessionId;

        public IncomingCallForm(int sessionId, string callerName, int callerId)
        {
            _sessionId = sessionId;
            Text            = "PISMO — Входящий звонок";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize      = new Size(340, 160);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = Color.FromArgb(47, 49, 54);
            Font            = new Font("Segoe UI", 9.5f);
            TopMost         = true;

            var lblIcon = new Label
            {
                Text = "📞",
                Font = new Font("Segoe UI", 28f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(60, 60),
                Location = new Point(20, 20)
            };

            var lblName = new Label
            {
                Text = callerName,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(92, 24)
            };

            var lblSub = new Label
            {
                Text = "Входящий звонок…",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize = true,
                Location = new Point(92, 52)
            };

            var btnAccept = new Button
            {
                Text = "✅ Принять",
                BackColor = Color.FromArgb(87, 171, 90),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Location = new Point(20, 100),
                Size = new Size(145, 42),
                Cursor = Cursors.Hand
            };
            btnAccept.FlatAppearance.BorderSize = 0;
            btnAccept.Click += (s, e) =>
            {
                Accepted = true;
                Close();
            };

            var btnDecline = new Button
            {
                Text = "❌ Отклонить",
                BackColor = Color.FromArgb(240, 71, 71),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Location = new Point(175, 100),
                Size = new Size(145, 42),
                Cursor = Cursors.Hand
            };
            btnDecline.FlatAppearance.BorderSize = 0;
            btnDecline.Click += (s, e) =>
            {
                Accepted = false;

                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "UPDATE call_sessions SET status='rejected', ended_at=NOW() WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@id", sessionId);
                    cmd.ExecuteNonQuery();
                    WebSocketSignalingClient.Instance.SendMessage("call_status", callerId, sessionId, "rejected");
                }
                catch { }

                Close();
            };

            Controls.AddRange(new Control[] { lblIcon, lblName, lblSub, btnAccept, btnDecline });

            // Мигание иконки для привлечения внимания
            _ringTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _ringTimer.Tick += (s, e) =>
            {
                _blinkState = 1 - _blinkState;
                lblIcon.Font = _blinkState == 0
                    ? new Font("Segoe UI", 28f)
                    : new Font("Segoe UI", 32f);
            };
            _ringTimer.Start();

            // Рингтон входящего звонка.
            try { Sounds.StartRingtone(); } catch { }

            // Обработчик отмены вызова со стороны звонящего
            WebSocketSignalingClient.Instance.OnMessageReceived += OnWebSocketMessage;
            
            // Отмена вызова мгновенно приходит по WS (call_status=ended); опрос БД —
            // страховка, и он идёт В ФОНЕ (раньше — синхронно на UI-потоке каждую
            // секунду, что подлагивало интерфейс, пока звонит рингтон).
            _checkTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _checkTimer.Tick += (s, e) =>
            {
                if (_checkBusy) return;
                _checkBusy = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    bool ended = false;
                    try
                    {
                        using var conn = DBHelper.OpenConnection();
                        using var cmd = new MySqlCommand("SELECT status FROM call_sessions WHERE id=@id", conn);
                        cmd.Parameters.AddWithValue("@id", sessionId);
                        var obj = cmd.ExecuteScalar();
                        ended = obj != null && obj.ToString() == "ended";
                    }
                    catch { }
                    finally { _checkBusy = false; }
                    if (!ended || IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke(new Action(() => { _checkTimer.Stop(); Close(); })); } catch { }
                });
            };
            _checkTimer.Start();

            FormClosed += (s, e) =>
            {
                try { Sounds.StopRingtone(); } catch { }
                _ringTimer.Stop();
                _checkTimer.Stop();
                _checkTimer.Dispose();
                WebSocketSignalingClient.Instance.OnMessageReceived -= OnWebSocketMessage;
            };
        }

        private void OnWebSocketMessage(string type, int senderId, int sId, string payload)
        {
            if (sId != _sessionId || IsDisposed) return;
            if (type == "call_status" && payload == "ended")
            {
                try { BeginInvoke(() => Close()); } catch { }
            }
        }
    }
}