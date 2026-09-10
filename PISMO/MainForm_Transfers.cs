// ============================================================
//  MainForm — кружок передач файлов (partial)
// ============================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    public partial class MainForm
    {
        // ── Кружок передач ───────────────────────────────────────────────
        //
        // Порт кружка с телефона. Показывает, сколько файлов идёт прямо
        // сейчас — в обе стороны, — и даёт их отменить, не возвращаясь в тот
        // чат, откуда всё началось.
        //
        // Место выбрано в шапке СПИСКА ЧАТОВ, а не над перепиской: передача
        // переживает переход в другой чат, и значок обязан быть виден оттуда
        // же. Плавающий поверх экрана кружок пробовали на телефоне — он
        // закрывал имена собеседников, и его убрали в шапку.

        private Button _btnTransfers;
        private Form _transfersPopup;
        private Panel _transfersList;
        private System.Windows.Forms.Timer _transfersTimer;

        private const int TransferRowH = 54;
        private const int TransferRowsMax = 8;

        private void BuildTransfersButton()
        {
            try
            {
                _btnTransfers = new Button
                {
                    Text = "⇅",
                    Dock = DockStyle.Right,
                    Width = 46,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(120, 200, 255),
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    Visible = false,
                };
                _btnTransfers.FlatAppearance.BorderSize = 0;
                new ToolTip().SetToolTip(_btnTransfers, "Идущие передачи файлов");
                _btnTransfers.Click += (s, e) => ShowTransfersPopup();
                pnlSidebarHeader.Controls.Add(_btnTransfers);
                _btnTransfers.BringToFront();

                // Опрос вместо событий: они приходили бы с потоков передач, и
                // каждую порцию пришлось бы переводить на поток окна. Четыре
                // раза в секунду прочитать список из памяти — ничто.
                _transfersTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _transfersTimer.Tick += (s, e) => RefreshTransfers();
                _transfersTimer.Start();
            }
            catch { }
        }

        private void RefreshTransfers()
        {
            try
            {
                int n = Transfers.Count;

                if (_btnTransfers != null)
                {
                    bool show = n > 0;
                    if (_btnTransfers.Visible != show) _btnTransfers.Visible = show;
                    if (show)
                    {
                        string t = "⇅ " + n;
                        if (_btnTransfers.Text != t) _btnTransfers.Text = t;
                    }
                }

                if (_transfersPopup is { IsDisposed: false } && _transfersList != null)
                {
                    if (n == 0) { _transfersPopup.Close(); return; }
                    int h = Math.Min(n, TransferRowsMax) * TransferRowH + 16;
                    if (_transfersPopup.ClientSize.Height != h)
                        _transfersPopup.ClientSize = new Size(_transfersPopup.ClientSize.Width, h);
                    _transfersList.Invalidate();
                }
            }
            catch { }
        }

        private void ShowTransfersPopup()
        {
            if (_transfersPopup is { IsDisposed: false })
            {
                try { _transfersPopup.Activate(); } catch { }
                return;
            }
            int n = Transfers.Count;
            if (n == 0) return;

            var pop = new Form
            {
                Text = "Передачи файлов",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(420, Math.Min(n, TransferRowsMax) * TransferRowH + 16),
                BackColor = Color.FromArgb(49, 51, 56),
                ShowInTaskbar = false,
            };

            var list = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(49, 51, 56) };
            EnableDoubleBuffer(list);
            list.Paint += (s, e) => PaintTransfers(e.Graphics, list.ClientSize.Width);
            list.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) HitTransfers(e.Location, list.ClientSize.Width);
            };
            pop.Controls.Add(list);

            _transfersPopup = pop;
            _transfersList = list;
            pop.FormClosed += (s, e) => { _transfersPopup = null; _transfersList = null; };

            // Под кнопкой, а если координаты почему-то не считаются — по центру.
            try
            {
                var p = _btnTransfers.PointToScreen(new Point(0, _btnTransfers.Height));
                pop.Location = new Point(Math.Max(0, p.X - 40), p.Y + 4);
            }
            catch { pop.StartPosition = FormStartPosition.CenterParent; }

            pop.Show(this);
        }

        /// <summary>Рисует весь список разом: перебирать контролы четыре раза в
        /// секунду значило бы мигание и мусор в памяти.</summary>
        private void PaintTransfers(Graphics g, int width)
        {
            var items = Transfers.Snapshot();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var fName = new Font("Segoe UI", 9f);
            using var fSmall = new Font("Segoe UI", 8f);
            using var fMark = new Font("Segoe UI", 11f, FontStyle.Bold);
            using var brName = new SolidBrush(Color.FromArgb(228, 230, 235));
            using var brMuted = new SolidBrush(Color.FromArgb(150, 152, 158));
            using var brUp = new SolidBrush(Color.FromArgb(88, 101, 242));
            using var brDown = new SolidBrush(Color.FromArgb(59, 165, 93));
            using var brTrack = new SolidBrush(Color.FromArgb(66, 69, 76));

            int barL = 34, barR = width - 44;
            for (int i = 0; i < items.Length && i < TransferRowsMax; i++)
            {
                var it = items[i];
                int y = 8 + i * TransferRowH;
                var accent = it.Upload ? brUp : brDown;

                g.DrawString(it.Upload ? "⬆" : "⬇", fMark, accent, 10, y + 4);

                // Имя обрезаем сами: длинное вылезало бы на крестик.
                string name = it.Name;
                var full = g.MeasureString(name, fName);
                int nameW = barR - barL - 70;
                if (full.Width > nameW)
                {
                    while (name.Length > 4 && g.MeasureString(name + "…", fName).Width > nameW)
                        name = name[..^1];
                    name += "…";
                }
                g.DrawString(name, fName, brName, barL, y + 2);

                double f = it.Fraction;
                string right = it.Cancelled ? "отмена…"
                    : f >= 0 ? (int)Math.Round(f * 100) + " %"
                    : it.Total > 0 ? FormatFileSize(it.Total) : "";
                var rs = g.MeasureString(right, fSmall);
                g.DrawString(right, fSmall, brMuted, barR - rs.Width, y + 4);

                // Полоса. Долю знаем только у отправки: скачивание идёт одним
                // запросом, и делить там нечего — вместо обмана бежит отрезок.
                int by = y + 26, bh = 6;
                g.FillRectangle(brTrack, barL, by, barR - barL, bh);
                if (f >= 0)
                {
                    int w = (int)((barR - barL) * f);
                    if (w > 0) g.FillRectangle(accent, barL, by, w, bh);
                }
                else
                {
                    int span = Math.Max(40, (barR - barL) / 4);
                    int travel = (barR - barL) + span;
                    int pos = (Environment.TickCount / 6) % travel - span;
                    int x1 = Math.Max(barL, barL + pos);
                    int x2 = Math.Min(barR, barL + pos + span);
                    if (x2 > x1) g.FillRectangle(accent, x1, by, x2 - x1, bh);
                }

                // Крестик отмены.
                using var pen = new Pen(Color.FromArgb(200, 120, 120), 2f);
                int cx = width - 30, cy = y + 12;
                g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                g.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
            }

            if (items.Length > TransferRowsMax)
            {
                g.DrawString("…и ещё " + (items.Length - TransferRowsMax), fSmall, brMuted,
                    barL, 8 + TransferRowsMax * TransferRowH - 14);
            }
        }

        private void HitTransfers(Point pt, int width)
        {
            var items = Transfers.Snapshot();
            int i = (pt.Y - 8) / TransferRowH;
            if (i < 0 || i >= items.Length || i >= TransferRowsMax) return;
            // Попадание по крестику — с запасом, целиться пиксель в пиксель
            // мышью никто не должен.
            if (pt.X < width - 42 || pt.X > width - 14) return;
            Transfers.Cancel(items[i]);
            _transfersList?.Invalidate();
        }
    }
}
