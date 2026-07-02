using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AForge.Video;
using AForge.Video.DirectShow;
using NAudio.Wave;

namespace PISMO
{
    // ДИЗАЙН формы (построение интерфейса). Вынесен из логики по образцу
    // MainForm.Designer.cs: здесь ТОЛЬКО создание/раскладка контролов.
    public partial class VideoCircleRecordForm
    {
        // ════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text            = "PISMO — Видео-кружочек";
            ClientSize      = new Size(360, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            // ── Заголовок ────────────────────────────────────────────
            var lblTitle = new Label
            {
                Text      = "🔵 Видео-кружочек",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            // ── Превью камеры ─────────────────────────────────────────
            _pbPreview = new PictureBox
            {
                BackColor = Color.FromArgb(32, 34, 37),
                SizeMode  = PictureBoxSizeMode.Zoom,
                Location  = new Point((ClientSize.Width - CircleSize) / 2, 52),
                Size      = new Size(CircleSize, CircleSize),
                Region    = new Region(GetCirclePath(CircleSize))
            };

            // ── Таймер ───────────────────────────────────────────────
            _lblTimer = new Label
            {
                Text      = "00:00 / 05:00",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(100, 386)
            };

            // ── Прогресс-бар ─────────────────────────────────────────
            _pbProgress = new ProgressBar
            {
                Location = new Point(20, 416),
                Size     = new Size(320, 8),
                Minimum  = 0,
                Maximum  = MaxSeconds,
                Value    = 0,
                Style    = ProgressBarStyle.Continuous
            };

            // ── Размер файла ──────────────────────────────────────────
            _lblSize = new Label
            {
                Text      = "Лимит: 5 мин • макс. ~30 МБ рекомендуется",
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(114, 118, 125),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(20, 430),
                Size      = new Size(320, 18)
            };

            // ── Подсказка ─────────────────────────────────────────────
            _lblHint = new Label
            {
                Text      = "Нажмите «Записать», чтобы начать (макс. 5 мин)",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(20, 452),
                Size      = new Size(320, 20)
            };

            // ── Кнопки ───────────────────────────────────────────────
            _btnRecord = new Button
            {
                Text      = "● Записать",
                BackColor = Color.FromArgb(240, 71, 71),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 486),
                Size      = new Size(160, 42),
                Cursor    = Cursors.Hand
            };
            _btnRecord.FlatAppearance.BorderSize = 0;
            _btnRecord.MouseEnter += (s, e) => _btnRecord.BackColor =
                _isRecording ? Color.FromArgb(180, 60, 60) : Color.FromArgb(200, 55, 55);
            _btnRecord.MouseLeave += (s, e) => _btnRecord.BackColor =
                _isRecording ? Color.FromArgb(79, 84, 92) : Color.FromArgb(240, 71, 71);
            _btnRecord.Click += BtnRecord_Click;

            _btnCancel = new Button
            {
                Text      = "Отмена",
                BackColor = Color.FromArgb(79, 84, 92),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Location  = new Point(190, 486),
                Size      = new Size(150, 42),
                Cursor    = Cursors.Hand
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.AddRange(new Control[]
            {
                lblTitle, _pbPreview,
                _lblTimer, _pbProgress, _lblSize, _lblHint,
                _btnRecord, _btnCancel
            });

            // ── Таймеры ───────────────────────────────────────────────
            _captureTimer = new System.Windows.Forms.Timer { Interval = 1000 / Fps };
            _captureTimer.Tick += CaptureTimer_Tick;

            _uiTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _uiTimer.Tick += UiTimer_Tick;
        }
    }
}
