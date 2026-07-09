using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Пикер эмодзи для реакций (2.1) в стиле Discord/Win+.: слева рейл
    /// категорий (🕒 часто используемые + каталог EmojiData), справа —
    /// прокручиваемая сетка ЦВЕТНЫХ эмодзи (EmojiRender). Клик — выбор и
    /// закрытие; Esc/клик мимо — отмена. Категории заполняются лениво —
    /// растеризация сотен эмодзи идёт только при первом входе в категорию.
    /// </summary>
    internal sealed class EmojiPickerForm : Form
    {
        public string SelectedEmoji { get; private set; }

        private readonly FlowLayoutPanel _grid;
        private readonly Label _lblTitle;
        private int _shownCategory = int.MinValue;   // -1 = «часто используемые»

        /// <summary>Показать пикер около точки экрана; вернёт эмодзи или null.</summary>
        public static string Pick(IWin32Window owner, Point screenPos)
        {
            try
            {
                using var f = new EmojiPickerForm();
                // Не вылезаем за рабочую область экрана.
                var wa = Screen.FromPoint(screenPos).WorkingArea;
                int x = Math.Min(screenPos.X, wa.Right - f.Width - 8);
                int y = Math.Min(screenPos.Y, wa.Bottom - f.Height - 8);
                f.Location = new Point(Math.Max(wa.Left + 8, x), Math.Max(wa.Top + 8, y));
                f.ShowDialog(owner);
                return f.SelectedEmoji;
            }
            catch { return null; }
        }

        private EmojiPickerForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.FromArgb(30, 31, 34);
            ClientSize = new Size(430, 420);
            Padding = new Padding(1);
            // Тонкая рамка, чтобы попап не сливался с фоном.
            Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(60, 62, 68));
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // ── Левый рейл категорий ──
            var rail = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                Width = 46,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(24, 25, 28),
                Padding = new Padding(4, 6, 0, 6)
            };

            _lblTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                ForeColor = Color.FromArgb(150, 152, 158),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                BackColor = Color.FromArgb(30, 31, 34)
            };

            _grid = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.FromArgb(30, 31, 34),
                Padding = new Padding(6, 0, 0, 6)
            };

            Button RailBtn(string icon, string tip)
            {
                var b = new Button
                {
                    Size = new Size(38, 38),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 0, 2),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                    BackgroundImageLayout = ImageLayout.Center
                };
                try { b.BackgroundImage = EmojiRender.Get(icon, 22); } catch { }
                if (b.BackgroundImage == null) { b.Text = icon; b.Font = new Font("Segoe UI Emoji", 12f); }
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(50, 52, 58);
                new ToolTip().SetToolTip(b, tip);
                return b;
            }

            var recentBtn = RailBtn("🕒", "Часто используемые");
            recentBtn.Click += (s, e) => ShowRecent();
            rail.Controls.Add(recentBtn);

            for (int i = 0; i < EmojiData.Categories.Length; i++)
            {
                int idx = i;
                var c = EmojiData.Categories[i];
                var b = RailBtn(c.Icon, c.Title);
                b.Click += (s, e) => ShowCategory(idx);
                rail.Controls.Add(b);
            }

            Controls.Add(_grid);
            Controls.Add(_lblTitle);
            Controls.Add(rail);

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += (s, e) => { try { Close(); } catch { } };

            ShowRecent();
        }

        private void ShowRecent() => Fill(-1, "Часто используемые", EmojiData.Recent().ToArray());

        private void ShowCategory(int idx)
        {
            var c = EmojiData.Categories[idx];
            Fill(idx, c.Title, c.Items);
        }

        private void Fill(int catId, string title, string[] items)
        {
            if (_shownCategory == catId) return;
            _shownCategory = catId;
            _lblTitle.Text = title;
            _grid.SuspendLayout();
            try
            {
                foreach (Control old in _grid.Controls) old.Dispose();
                _grid.Controls.Clear();
                foreach (var emoji in items)
                {
                    string em = emoji;
                    var b = new Button
                    {
                        Size = new Size(40, 40),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.Transparent,
                        Margin = new Padding(1),
                        Cursor = Cursors.Hand,
                        TabStop = false,
                        BackgroundImageLayout = ImageLayout.Center
                    };
                    try { b.BackgroundImage = EmojiRender.Get(em, 26); } catch { }
                    if (b.BackgroundImage == null) { b.Text = em; b.Font = new Font("Segoe UI Emoji", 14f); }
                    b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 58, 64);
                    b.Click += (s, e) =>
                    {
                        SelectedEmoji = em;
                        EmojiData.AddRecent(em);
                        DialogResult = DialogResult.OK;
                        Close();
                    };
                    _grid.Controls.Add(b);
                }
                _grid.VerticalScroll.Value = 0;
            }
            catch { }
            finally { _grid.ResumeLayout(); }
        }
    }
}
