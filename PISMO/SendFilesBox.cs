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
            ClientSize = new Size(560, 520);
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
            // Альбом или файлы — вид пачки меняется сразу.
            _asFiles.CheckedChanged += (s, e) => Rebuild();

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

            // КАК УЙДЁТ — ТАК И ПОКАЗЫВАЕМ.
            //
            // Снимки и ролики уходят альбомом, и окно рисует их той же
            // мозаикой, что встанет в переписке: ряды разной длины, без
            // пустых клеток, по десять в плитку — как в Telegram Desktop.
            // Файлы и «как файлы» — обычной сеткой одинаковых плиток.
            bool allMedia = !_asFiles.Checked && _files.Count > 1 && _files.All(IsMedia);
            if (allMedia)
            {
                int w = Math.Max(240, _strip.ClientSize.Width - _strip.Padding.Horizontal
                                     - SystemInformation.VerticalScrollBarWidth - 4);
                const int gap = 3;
                int order = 0;
                for (int from = 0; from < _files.Count; from += 10)
                {
                    int n = Math.Min(10, _files.Count - from);
                    int taken = 0;
                    foreach (int count in RowSplit(n))
                    {
                        int side = (w - gap * (count - 1)) / count;
                        // Ряд из двух — пониже квадрата, иначе альбом из
                        // четырёх снимков занимал бы весь экран.
                        int h = count <= 2 ? (int)(side * 0.75) : side;
                        for (int c = 0; c < count; c++)
                        {
                            var tile = MakeTile(_files[from + taken], ++order, side, h);
                            tile.Margin = new Padding(0, 0, c < count - 1 ? gap : 0, gap);
                            _strip.Controls.Add(tile);
                            taken++;
                            // Последняя клетка ряда — перенос строки.
                            if (c == count - 1) _strip.SetFlowBreak(tile, true);
                        }
                    }
                    // Между плитками по десять — заметный зазор.
                    if (_strip.Controls.Count > 0)
                        _strip.Controls[_strip.Controls.Count - 1].Margin = new Padding(0, 0, 0, 14);
                }
            }
            else
            {
                for (int i = 0; i < _files.Count; i++)
                {
                    var tile = MakeTile(_files[i], i + 1, 104, 104);
                    tile.Margin = new Padding(5);
                    _strip.Controls.Add(tile);
                }
            }
            _strip.ResumeLayout();
        }

        private static bool IsMedia(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return (Array.IndexOf(ImageExt, ext) >= 0 && ext != ".gif")
                   || MediaPlayerForm.IsVideo(ext.TrimStart('.'));
        }

        /// <summary>Длины рядов мозаики — то же правило, что у плитки в ленте.</summary>
        private static List<int> RowSplit(int n)
        {
            var res = new List<int>();
            if (n <= 0) return res;
            if (n <= 3) { res.Add(n); return res; }
            int rows = (n + 2) / 3;
            int bas = n / rows, rem = n % rows;
            for (int i = 0; i < rows; i++) res.Add(i < rem ? bas + 1 : bas);
            return res;
        }

        private static string Plural(int n)
        {
            int n10 = n % 10, n100 = n % 100;
            if (n10 == 1 && n100 != 11) return "файл";
            if (n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14)) return "файла";
            return "файлов";
        }

        /// <summary>
        /// Плитка предпросмотра любого размера: снимок или кадр ролика,
        /// обрезанные под её форму, либо карточка файла; номер и крестик.
        /// Углы скруглены — как у плиток в ленте и над полем ввода.
        /// </summary>
        private Control MakeTile(string path, int order, int tw, int th)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isImg = Array.IndexOf(ImageExt, ext) >= 0;
            // Ролик показывается КАДРОМ, как в телеграме: иначе в пачке он
            // неотличим от любого другого файла (см. VideoCover).
            bool isVid = !isImg && MediaPlayerForm.IsVideo(ext.TrimStart('.'));

            var tile = new Panel
            {
                Size = new Size(tw, th),
                BackColor = Color.FromArgb(32, 34, 37),
                Cursor = Cursors.Hand,
            };
            tile.SetStyle_DoubleBuffer();

            Image thumb = null;
            try
            {
                if (isVid)
                {
                    var jpeg = VideoCover.Jpeg(path, Math.Max(tw, th) * 2);
                    if (jpeg != null)
                    {
                        using var ms = new MemoryStream(jpeg);
                        using var src = Image.FromStream(ms);
                        thumb = CropTo(src, tw, th);
                    }
                }
                else if (isImg)
                {
                    using var src = Image.FromFile(path);
                    thumb = CropTo(src, tw, th);
                }
            }
            catch { thumb = null; }

            string name = Path.GetFileName(path);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            var closeRect = new Rectangle(tw - 27, 5, 22, 22);

            tile.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(tile.BackColor);
                using (var path0 = Round(new Rectangle(0, 0, tw - 1, th - 1), 8))
                {
                    var save = g.Save();
                    g.SetClip(path0);
                    if (thumb != null)
                    {
                        g.DrawImage(thumb, 0, 0, tw, th);
                    }
                    else
                    {
                        using var bg = new SolidBrush(Color.FromArgb(44, 47, 51));
                        g.FillRectangle(bg, 0, 0, tw, th);
                        string label = ext.TrimStart('.').ToUpperInvariant();
                        if (label.Length == 0) label = "FILE";
                        using var f1 = new Font("Segoe UI Black", 13f, FontStyle.Bold);
                        using var sf = new StringFormat
                        { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(label, f1, Brushes.White, new RectangleF(0, th / 2f - 34, tw, 32), sf);
                        using var f2 = new Font("Segoe UI", 7.5f);
                        using var br = new SolidBrush(Color.FromArgb(190, 193, 199));
                        g.DrawString(Shorten(name, 16) + "\n" + MainForm.FormatFileSize(size), f2, br,
                                     new RectangleF(4, th / 2f, tw - 8, 44), sf);
                    }
                    g.Restore(save);
                }

                if (isVid && thumb != null)
                {
                    int cx = tw / 2, cy = th / 2;
                    using var disc = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                    g.FillEllipse(disc, cx - 17, cy - 17, 34, 34);
                    g.FillPolygon(Brushes.White, new[]
                    {
                        new Point(cx - 6, cy - 9), new Point(cx - 6, cy + 9), new Point(cx + 11, cy),
                    });
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
                    g.FillEllipse(xb, closeRect);
                using (var pen = new Pen(Color.White, 1.8f))
                {
                    g.DrawLine(pen, closeRect.X + 6, closeRect.Y + 6, closeRect.Right - 6, closeRect.Bottom - 6);
                    g.DrawLine(pen, closeRect.Right - 6, closeRect.Y + 6, closeRect.X + 6, closeRect.Bottom - 6);
                }
            };

            tile.MouseClick += (s, e) =>
            {
                if (closeRect.Contains(e.Location))
                {
                    _files.Remove(path);
                    if (_files.Count == 0) { DialogResult = DialogResult.Cancel; Close(); return; }
                    Rebuild();
                    return;
                }
                // Нажатие по самой плитке — предпросмотр с редактором.
                PreviewFile(path);
            };
            tile.Disposed += (s, e) => { try { thumb?.Dispose(); } catch { } };
            new ToolTip().SetToolTip(tile, name);
            return tile;
        }

        /// <summary>Картинка, обрезанная по центру под форму плитки.</summary>
        private static Bitmap CropTo(Image src, int w, int h)
        {
            float want = w / (float)h, have = src.Width / (float)src.Height;
            int cw = src.Width, ch = src.Height;
            if (have > want) cw = (int)(src.Height * want); else ch = (int)(src.Width / want);
            int sx = (src.Width - cw) / 2, sy = (src.Height - ch) / 2;
            var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, w, h),
                        new Rectangle(sx, sy, cw, ch), GraphicsUnit.Pixel);
            return bmp;
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>
        /// Предпросмотр перед отправкой: снимок открывается в редакторе —
        /// правка возвращается в ту же пачку, — а ролик в плеере. Раньше
        /// пачка уходила вслепую.
        /// </summary>
        private void PreviewFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isImg = Array.IndexOf(ImageExt, ext) >= 0 && ext != ".gif";
            try
            {
                if (isImg)
                {
                    using var ed = new PhotoEditorForm(File.ReadAllBytes(path));
                    if (ed.ShowDialog(this) != DialogResult.OK) return;
                    var res = ed.Result;
                    if (res == null || res.Length == 0) return;
                    string tmp = Path.Combine(Path.GetTempPath(),
                        "pismo-edit-" + Guid.NewGuid().ToString("N") + ".jpg");
                    File.WriteAllBytes(tmp, res);
                    int i = _files.IndexOf(path);
                    if (i >= 0) _files[i] = tmp;
                    Rebuild();
                    return;
                }

                if (MediaPlayerForm.IsVideo(ext.TrimStart('.')))
                {
                    using var win = new Form
                    {
                        Text = Path.GetFileName(path),
                        StartPosition = FormStartPosition.CenterParent,
                        ClientSize = new Size(880, 540),
                        BackColor = Color.FromArgb(20, 21, 24),
                        ShowInTaskbar = false,
                        MinimizeBox = false,
                    };
                    var capture = path;
                    var vp = new InlineVideoPlayer(() => File.ReadAllBytes(capture),
                                                   Path.GetFileName(path), 860, 520)
                    {
                        Dock = DockStyle.Fill,
                        InlinePage = false,
                    };
                    win.Controls.Add(vp);
                    win.ShowDialog(this);
                }
            }
            catch { }
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
