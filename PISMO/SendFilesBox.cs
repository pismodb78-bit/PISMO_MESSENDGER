// ============================================================
//  SendFilesBox — окно подтверждения отправки вложений
// ============================================================
//
//  Как это устроено в Telegram Desktop: скрепка открывает ОБЫЧНЫЙ системный
//  выбор файлов, а дальше появляется окно «Отправить N файлов?» — с
//  предпросмотром выбранного, подписью и переключателем «как файлы» (то есть
//  без сжатия, документом). Там же можно убрать лишнее и добавить ещё.
//
//  Своя сетка галереи, которая была здесь до этого, — приём ТЕЛЕФОНА: на
//  компьютере человек ищет файлы системным проводником, и подменять его
//  собственным списком папок незачем.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class SendFilesBox : Form
    {
        private static readonly string[] ImageExt =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        private readonly List<string> _files;
        private readonly FlowLayoutPanel _strip = new();
        private readonly Label _title = new();
        private readonly CheckBox _asFiles = new();
        private readonly TextBox _caption = new();

        /// <summary>Что отправляем — в порядке выбора.</summary>
        public IReadOnlyList<string> Files => _files;

        /// <summary>Подпись к пачке (уходит первому вложению).</summary>
        public string Caption => _caption.Text.Trim();

        /// <summary>Отправить документами, без превью-картинок.</summary>
        public bool AsFiles => _asFiles.Checked;

        private SendFilesBox(IEnumerable<string> files)
        {
            _files = files.Where(File.Exists).ToList();

            Text = "Отправка";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 330);
            BackColor = Color.FromArgb(32, 34, 37);
            ForeColor = Color.FromArgb(220, 222, 225);
            ShowInTaskbar = false;
            KeyPreview = true;

            _title.Dock = DockStyle.Top;
            _title.Height = 36;
            _title.Padding = new Padding(14, 10, 0, 0);
            _title.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            _title.ForeColor = Color.FromArgb(230, 232, 235);

            _strip.Dock = DockStyle.Fill;
            _strip.AutoScroll = true;
            _strip.WrapContents = true;
            _strip.Padding = new Padding(10);
            _strip.BackColor = Color.FromArgb(32, 34, 37);

            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 118,
                Padding = new Padding(12),
                BackColor = Color.FromArgb(24, 25, 28),
            };

            var row = new Panel { Dock = DockStyle.Bottom, Height = 36 };
            var send = new Button { Text = "Отправить", Dock = DockStyle.Right, Width = 150 };
            Flat(send, Color.FromArgb(88, 101, 242));
            send.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var cancel = new Button { Text = "Отмена", Dock = DockStyle.Right, Width = 110 };
            Flat(cancel, Color.FromArgb(57, 60, 66));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            var add = new Button { Text = "Добавить ещё…", Dock = DockStyle.Left, Width = 150 };
            Flat(add, Color.FromArgb(57, 60, 66));
            add.Click += (s, e) => AddMore();
            row.Controls.Add(add);
            row.Controls.Add(cancel);
            row.Controls.Add(send);

            _asFiles.Text = "Отправить как файлы (без сжатия)";
            _asFiles.Dock = DockStyle.Bottom;
            _asFiles.Height = 26;
            _asFiles.ForeColor = Color.FromArgb(200, 203, 209);
            _asFiles.Font = new Font("Segoe UI", 9f);

            _caption.Dock = DockStyle.Fill;
            _caption.Multiline = true;
            _caption.BackColor = Color.FromArgb(44, 47, 51);
            _caption.ForeColor = Color.FromArgb(220, 222, 225);
            _caption.BorderStyle = BorderStyle.FixedSingle;
            _caption.PlaceholderText = "Подпись…";

            bottom.Controls.Add(_caption);
            bottom.Controls.Add(_asFiles);
            bottom.Controls.Add(row);

            Controls.Add(_strip);
            Controls.Add(bottom);
            Controls.Add(_title);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter && e.Control)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            Rebuild();
        }

        private static void Flat(Button b, Color back)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9f);
            b.Cursor = Cursors.Hand;
            b.Margin = new Padding(6, 0, 6, 0);
        }

        /// <summary>
        /// Спросить про отправку. false — передумали.
        /// </summary>
        public static bool Ask(IWin32Window owner, IEnumerable<string> files,
                               out List<string> result, out string caption, out bool asFiles)
        {
            result = new List<string>();
            caption = "";
            asFiles = false;

            using var box = new SendFilesBox(files);
            if (box._files.Count == 0) return false;
            if (box.ShowDialog(owner) != DialogResult.OK) return false;
            if (box._files.Count == 0) return false;

            result = box._files.ToList();
            caption = box.Caption;
            asFiles = box.AsFiles;
            return true;
        }

        private void AddMore()
        {
            using var dlg = new OpenFileDialog { Title = "Добавить файлы", Multiselect = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in dlg.FileNames)
                if (File.Exists(f) && !_files.Contains(f)) _files.Add(f);
            Rebuild();
        }

        private void Rebuild()
        {
            _title.Text = _files.Count == 1
                ? "Отправить файл?"
                : $"Отправить {_files.Count} {Plural(_files.Count)}?";

            _strip.SuspendLayout();
            foreach (Control c in _strip.Controls.Cast<Control>().ToList())
            {
                _strip.Controls.Remove(c);
                c.Dispose();
            }

            for (int i = 0; i < _files.Count; i++)
            {
                string path = _files[i];
                _strip.Controls.Add(MakeTile(path, i + 1));
            }
            _strip.ResumeLayout();
        }

        private static string Plural(int n)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return "файл";
            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return "файла";
            return "файлов";
        }

        /// <summary>Плитка предпросмотра: снимок или карточка файла, номер и крестик.</summary>
        private Control MakeTile(string path, int order)
        {
            const int side = 104;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isImg = Array.IndexOf(ImageExt, ext) >= 0;

            var tile = new Panel
            {
                Size = new Size(side, side),
                Margin = new Padding(5),
                BackColor = Color.FromArgb(23, 24, 28),
            };
            tile.SetStyle_DoubleBuffer();

            Image thumb = null;
            if (isImg)
            {
                try
                {
                    using var src = Image.FromFile(path);
                    int cut = Math.Min(src.Width, src.Height);
                    int sx = (src.Width - cut) / 2, sy = (src.Height - cut) / 2;
                    var bmp = new Bitmap(side, side);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, side, side),
                                    new Rectangle(sx, sy, cut, cut), GraphicsUnit.Pixel);
                    }
                    thumb = bmp;
                }
                catch { thumb = null; }
            }

            string name = Path.GetFileName(path);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            tile.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (thumb != null) g.DrawImage(thumb, 0, 0, side, side);
                else
                {
                    using var bg = new SolidBrush(Color.FromArgb(44, 47, 51));
                    g.FillRectangle(bg, 0, 0, side, side);
                    string label = ext.TrimStart('.').ToUpperInvariant();
                    if (label.Length == 0) label = "FILE";
                    using var f1 = new Font("Segoe UI Black", 13f, FontStyle.Bold);
                    using var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(label, f1, Brushes.White, new RectangleF(0, 14, side, 32), sf);
                    using var f2 = new Font("Segoe UI", 7.5f);
                    using var br = new SolidBrush(Color.FromArgb(190, 193, 199));
                    g.DrawString(Shorten(name, 16) + "\n" + MainForm.FormatFileSize(size), f2, br,
                                 new RectangleF(4, 52, side - 8, 44), sf);
                }

                // Номер по порядку отправки.
                using (var nb = new SolidBrush(Color.FromArgb(30, 143, 225)))
                    g.FillEllipse(nb, 5, 5, 22, 22);
                using (var f3 = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold))
                using (var sf2 = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(order.ToString(), f3, Brushes.White, new RectangleF(5, 5, 22, 22), sf2);

                // Крестик.
                using (var xb = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                    g.FillEllipse(xb, side - 27, 5, 22, 22);
                using (var pen = new Pen(Color.White, 1.8f))
                {
                    g.DrawLine(pen, side - 21, 11, side - 11, 21);
                    g.DrawLine(pen, side - 11, 11, side - 21, 21);
                }
            };

            tile.MouseClick += (s, e) =>
            {
                var close = new Rectangle(side - 27, 5, 22, 22);
                if (close.Contains(e.Location))
                {
                    _files.Remove(path);
                    if (_files.Count == 0) { DialogResult = DialogResult.Cancel; Close(); return; }
                    Rebuild();
                }
            };
            tile.Disposed += (s, e) => { try { thumb?.Dispose(); } catch { } };
            new ToolTip().SetToolTip(tile, name);
            return tile;
        }

        private static string Shorten(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    internal static class PanelStyleExt
    {
        /// <summary>Двойная буферизация для рисуемой вручную плитки.</summary>
        public static void SetStyle_DoubleBuffer(this Panel p)
        {
            try
            {
                typeof(Panel).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(p, true);
            }
            catch { }
        }
    }
}
