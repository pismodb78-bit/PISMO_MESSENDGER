using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Свой выбор источника демонстрации — прокручиваемый список ВСЕХ мониторов
    /// (как плитка окон в системном диалоге, которой для экранов не было).
    /// Ловит каждый монитор, который Windows показывает в «Параметрах дисплея»,
    /// включая виртуальные VR-дисплеи (Virtual Desktop), которые встроенный
    /// диалог getDisplayMedia перечисляет не всегда. Плюс кнопка «Окно / другой
    /// источник» — откат на системный выбор (там удобная плитка окон).
    /// </summary>
    public class ScreenPickerForm : Form
    {
        /// <summary>Границы выбранного монитора (в координатах виртуального рабочего
        /// стола), либо null — если пользователь выбрал системный диалог/отмену.</summary>
        public Rectangle? SelectedBounds { get; private set; }

        /// <summary>Пользователь выбрал «Окно / другой источник» — открыть системный
        /// диалог getDisplayMedia вместо захвата конкретного монитора.</summary>
        public bool UseSystemPicker { get; private set; }

        public ScreenPickerForm()
        {
            Text = "Выберите, что транслировать";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(24, 25, 28);
            ForeColor = Color.FromArgb(220, 221, 222);
            // Ширина рассчитана на 2 крупные плитки в ряд + вертикальный скроллбар;
            // 3-й монитор переносится на второй ряд и достигается прокруткой вниз.
            ClientSize = new Size(784, 540);

            var title = new Label
            {
                Text = "Экраны",
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(16, 12, 0, 0),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(235, 236, 238)
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12),
                BackColor = Color.FromArgb(24, 25, 28)
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(30, 31, 34) };
            var btnWindow = new Button
            {
                Text = "🪟  Окно или другой источник…",
                AutoSize = false,
                Size = new Size(280, 34),
                Location = new Point(16, 11),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };
            btnWindow.FlatAppearance.BorderSize = 0;
            btnWindow.Click += (s, e) => { UseSystemPicker = true; DialogResult = DialogResult.OK; Close(); };

            var btnCancel = new Button
            {
                Text = "Отмена",
                Size = new Size(110, 34),
                Location = new Point(ClientSize.Width - 126, 11),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 47, 51),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            bottom.Controls.Add(btnWindow);
            bottom.Controls.Add(btnCancel);

            Controls.Add(flow);
            Controls.Add(bottom);
            Controls.Add(title);

            BuildTiles(flow);
        }

        private void BuildTiles(FlowLayoutPanel flow)
        {
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var scr = screens[i];
                var bounds = scr.Bounds;

                // Крупные плитки как в системном «Весь экран»: по 2 в ряд, при
                // большем числе мониторов переносятся вниз (вертикальная прокрутка
                // FlowLayoutPanel), а не жмутся мелкими в одну строку.
                var tile = new Panel
                {
                    Size = new Size(348, 250),
                    Margin = new Padding(8),
                    BackColor = Color.FromArgb(32, 34, 37),
                    Cursor = Cursors.Hand
                };

                var pb = new PictureBox
                {
                    Dock = DockStyle.Top,
                    Height = 196,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.FromArgb(15, 16, 18),
                    Cursor = Cursors.Hand
                };
                try { pb.Image = GrabThumbnail(bounds, 344, 194); } catch { }

                string tag = scr.Primary ? "  (основной)" : "";
                var cap = new Label
                {
                    Text = $"Экран {i + 1} — {bounds.Width}×{bounds.Height}{tag}",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(210, 212, 216),
                    Font = new Font("Segoe UI", 9.5f),
                    Cursor = Cursors.Hand
                };

                void Choose(object s, EventArgs e)
                {
                    SelectedBounds = bounds;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                tile.Click += Choose;
                pb.Click += Choose;
                cap.Click += Choose;

                // Подсветка при наведении.
                void Hi(object s, EventArgs e) => tile.BackColor = Color.FromArgb(47, 49, 54);
                void Lo(object s, EventArgs e) => tile.BackColor = Color.FromArgb(32, 34, 37);
                tile.MouseEnter += Hi; tile.MouseLeave += Lo;
                pb.MouseEnter += Hi; cap.MouseEnter += Hi;

                tile.Controls.Add(cap);
                tile.Controls.Add(pb);
                flow.Controls.Add(tile);
            }
        }

        /// <summary>Одноразовый снимок монитора для превью-плитки (GDI, совместимо
        /// с виртуальными дисплеями).</summary>
        private static Image GrabThumbnail(Rectangle bounds, int w, int h)
        {
            using var full = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

            double ratio = Math.Min((double)w / bounds.Width, (double)h / bounds.Height);
            int tw = Math.Max(1, (int)(bounds.Width * ratio));
            int th = Math.Max(1, (int)(bounds.Height * ratio));
            var thumb = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, tw, th);
            }
            return thumb;
        }
    }
}
