// ============================================================
//  AttachPickerForm — выбор вложений сеткой, как в Telegram
// ============================================================
//
//  Раньше скрепка открывала системный диалог выбора файлов: список имён, из
//  которого не видно ни что выбираешь, ни в каком порядке оно уйдёт. На
//  телефоне это уже свой лист с плиткой (ui/chat/AttachSheet.kt), и здесь
//  теперь то же самое:
//
//    • сетка последних снимков и роликов из «Изображений», «Видео»,
//      «Загрузок» и рабочего стола;
//    • кружок справа сверху — ВЫБОР, и в нём НОМЕР: в этом порядке пачка и
//      уйдёт;
//    • нажатие в середину плитки — ПРОСМОТР, чтобы не отправлять вслепую;
//    • вкладка «Файлы» для всего остального — выбор у вкладок ОБЩИЙ, и
//      номера переживают переход туда и обратно;
//    • подпись пишется здесь же и уходит вместе с пачкой;
//    • выход при непустом выборе спрашивает, сбрасывать ли его.
//
//  Миниатюры грузятся в фоне: папка «Изображения» бывает на тысячи снимков,
//  и читать их на потоке окна значит показать пустое окно на полминуты.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class AttachPickerForm : Form
    {
        private static readonly string[] ImageExt =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
        private static readonly string[] VideoExt =
            { ".mp4", ".webm", ".m4v", ".mov", ".mkv", ".3gp" };

        // Плитка выбора — крупная: на мелкой не разглядеть, что именно
        // отмечаешь, а окно всё равно на пол-экрана.
        private const int TileSide = 196;
        private const int MaxScan = 400;

        /// <summary>Выбранное — В ПОРЯДКЕ ВЫБОРА.</summary>
        public List<string> Picked { get; } = new();

        /// <summary>Подпись к пачке (уходит первому вложению).</summary>
        public string Caption => _caption.Text.Trim();

        /// <summary>Нажали «Отправить», а не «Прикрепить».</summary>
        public bool SendNow { get; private set; }

        private readonly Label _title = new();
        private readonly Button _tabMedia = new();
        private readonly Button _tabFiles = new();
        private readonly Button _reset = new();
        private readonly Panel _mediaPage = new();
        private readonly FlowLayoutPanel _grid = new();
        private readonly Panel _filesPage = new();
        private readonly FlowLayoutPanel _filesList = new();
        private readonly TextBox _caption = new();
        private readonly Button _send = new();
        private readonly Button _attachOnly = new();

        private readonly List<Tile> _tiles = new();
        private readonly CancellationTokenSource _cts = new();

        public AttachPickerForm()
        {
            Text = "Вложения";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(940, 660);
            MinimumSize = new Size(640, 480);
            BackColor = Color.FromArgb(32, 34, 37);
            ForeColor = Color.FromArgb(220, 222, 225);
            KeyPreview = true;
            ShowInTaskbar = false;

            // ── Шапка ────────────────────────────────────────────────
            var head = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(24, 25, 28) };
            _title.Text = "Галерея";
            _title.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            _title.ForeColor = Color.FromArgb(230, 232, 235);
            _title.AutoSize = true;
            _title.Location = new Point(14, 13);
            head.Controls.Add(_title);

            StyleTab(_tabMedia, "Фото и видео", 220);
            StyleTab(_tabFiles, "Файлы", 360);
            _tabMedia.Click += (s, e) => ShowTab(media: true);
            _tabFiles.Click += (s, e) => ShowTab(media: false);
            head.Controls.Add(_tabMedia);
            head.Controls.Add(_tabFiles);

            _reset.Text = "Сбросить";
            _reset.Size = new Size(96, 28);
            _reset.Location = new Point(ClientSize.Width - 112, 9);
            _reset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Flat(_reset, Color.FromArgb(57, 60, 66));
            _reset.Click += (s, e) => { Picked.Clear(); RefreshState(); };
            head.Controls.Add(_reset);

            // ── Низ: подпись и отправка ──────────────────────────────
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Color.FromArgb(24, 25, 28) };
            _caption.Location = new Point(14, 14);
            _caption.Size = new Size(ClientSize.Width - 320, 26);
            _caption.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _caption.BackColor = Color.FromArgb(44, 47, 51);
            _caption.ForeColor = Color.FromArgb(220, 222, 225);
            _caption.BorderStyle = BorderStyle.FixedSingle;
            _caption.PlaceholderText = "Добавить подпись…";
            foot.Controls.Add(_caption);

            _attachOnly.Text = "Прикрепить";
            _attachOnly.Size = new Size(120, 28);
            _attachOnly.Location = new Point(ClientSize.Width - 290, 13);
            _attachOnly.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Flat(_attachOnly, Color.FromArgb(57, 60, 66));
            _attachOnly.Click += (s, e) => Finish(send: false);
            foot.Controls.Add(_attachOnly);

            _send.Text = "Отправить";
            _send.Size = new Size(150, 28);
            _send.Location = new Point(ClientSize.Width - 160, 13);
            _send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Flat(_send, Color.FromArgb(88, 101, 242));
            _send.Click += (s, e) => Finish(send: true);
            foot.Controls.Add(_send);

            // ── Сетка медиа ──────────────────────────────────────────
            _grid.Dock = DockStyle.Fill;
            _grid.AutoScroll = true;
            _grid.WrapContents = true;
            _grid.Padding = new Padding(8);
            _grid.BackColor = Color.FromArgb(32, 34, 37);
            _mediaPage.Dock = DockStyle.Fill;
            _mediaPage.Controls.Add(_grid);

            // ── Вкладка «Файлы» ──────────────────────────────────────
            var browse = new Button
            {
                Text = "Внутреннее хранилище — выбрать файлы…",
                Dock = DockStyle.Top,
                Height = 40,
            };
            Flat(browse, Color.FromArgb(47, 49, 54));
            browse.Click += (s, e) => BrowseFiles();
            _filesList.Dock = DockStyle.Fill;
            _filesList.AutoScroll = true;
            _filesList.WrapContents = false;
            _filesList.FlowDirection = FlowDirection.TopDown;
            _filesList.BackColor = Color.FromArgb(32, 34, 37);
            _filesPage.Dock = DockStyle.Fill;
            _filesPage.Visible = false;
            _filesPage.Controls.Add(_filesList);
            _filesPage.Controls.Add(browse);

            Controls.Add(_mediaPage);
            Controls.Add(_filesPage);
            Controls.Add(foot);
            Controls.Add(head);

            ShowTab(media: true);
            RefreshState();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += (s, e) => LoadGalleryAsync();
            FormClosing += OnClosingGuard;
            FormClosed += (s, e) => { try { _cts.Cancel(); } catch { } };
        }

        private static void Flat(Button b, Color back)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9f);
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
        }

        private void StyleTab(Button b, string text, int x)
        {
            b.Text = text;
            b.Size = new Size(130, 28);
            b.Location = new Point(x, 9);
            Flat(b, Color.FromArgb(47, 49, 54));
        }

        private void ShowTab(bool media)
        {
            _mediaPage.Visible = media;
            _filesPage.Visible = !media;
            _tabMedia.BackColor = media ? Color.FromArgb(88, 101, 242) : Color.FromArgb(47, 49, 54);
            _tabFiles.BackColor = media ? Color.FromArgb(47, 49, 54) : Color.FromArgb(88, 101, 242);
            if (!media) RebuildFilesList();
        }

        // ── Сбор галереи ─────────────────────────────────────────────
        private void LoadGalleryAsync()
        {
            var token = _cts.Token;
            Task.Run(() =>
            {
                var found = new List<string>();
                foreach (var dir in GalleryFolders())
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                        {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            if (Array.IndexOf(ImageExt, ext) < 0 && Array.IndexOf(VideoExt, ext) < 0) continue;
                            found.Add(f);
                            if (found.Count > MaxScan * 3) break;
                        }
                    }
                    catch { }
                }

                List<string> sorted;
                try
                {
                    sorted = found
                        .Select(f => (path: f, time: SafeTime(f)))
                        .OrderByDescending(x => x.time)
                        .Take(MaxScan)
                        .Select(x => x.path)
                        .ToList();
                }
                catch { sorted = found.Take(MaxScan).ToList(); }

                if (token.IsCancellationRequested || IsDisposed) return;
                try { BeginInvoke(new Action(() => FillGrid(sorted))); } catch { }
            });
        }

        private static DateTime SafeTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
        }

        private static IEnumerable<string> GalleryFolders()
        {
            var list = new List<string>();
            void Add(Environment.SpecialFolder f)
            {
                try
                {
                    string p = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(p);
                }
                catch { }
            }
            Add(Environment.SpecialFolder.MyPictures);
            Add(Environment.SpecialFolder.MyVideos);
            Add(Environment.SpecialFolder.Desktop);
            try
            {
                string dl = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl)) list.Add(dl);
            }
            catch { }
            return list.Distinct();
        }

        private void FillGrid(List<string> files)
        {
            if (IsDisposed) return;
            _grid.SuspendLayout();
            foreach (var f in files)
            {
                var tile = new Tile(f, TileSide);
                tile.ToggleRequested += p => Toggle(p);
                tile.OpenRequested += Preview;
                _tiles.Add(tile);
                _grid.Controls.Add(tile);
            }
            _grid.ResumeLayout();
            RefreshState();

            // Миниатюры — в фоне и по одной: сотня снимков по десять
            // мегапикселей, прочитанная разом, съела бы память окна.
            var token = _cts.Token;
            Task.Run(() =>
            {
                foreach (var t in _tiles.ToList())
                {
                    if (token.IsCancellationRequested) return;
                    Bitmap bmp = null;
                    try { bmp = Thumb(t.Path, TileSide); } catch { }
                    if (bmp == null || token.IsCancellationRequested) { bmp?.Dispose(); continue; }
                    try { t.BeginInvoke(new Action(() => t.SetThumb(bmp))); }
                    catch { bmp.Dispose(); }
                }
            });
        }

        private static Bitmap Thumb(string path, int side)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(VideoExt, ext) >= 0) return null;   // кадр из ролика взять нечем
            using var src = Image.FromFile(path);
            int cut = Math.Min(src.Width, src.Height);
            int sx = (src.Width - cut) / 2, sy = (src.Height - cut) / 2;
            var dst = new Bitmap(side, side);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, side, side),
                        new Rectangle(sx, sy, cut, cut), GraphicsUnit.Pixel);
            return dst;
        }

        // ── Выбор ────────────────────────────────────────────────────
        private void Toggle(string path)
        {
            if (Picked.Contains(path)) Picked.Remove(path);
            else Picked.Add(path);
            RefreshState();
        }

        private void RefreshState()
        {
            _title.Text = Picked.Count == 0 ? "Галерея" : $"Выбрано {Picked.Count}";
            _reset.Visible = Picked.Count > 0;
            _send.Enabled = Picked.Count > 0;
            _attachOnly.Enabled = Picked.Count > 0;
            _send.Text = Picked.Count > 0 ? $"Отправить ({Picked.Count})" : "Отправить";
            foreach (var t in _tiles) t.SetOrder(Picked.IndexOf(t.Path) + 1);
            if (_filesPage.Visible) RebuildFilesList();
        }

        private void Preview(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(VideoExt, ext) >= 0)
                {
                    new MediaPlayerForm(File.ReadAllBytes(path), Path.GetFileName(path), true).Show(this);
                    return;
                }
                MainForm.ShowImageFullscreen(File.ReadAllBytes(path));
            }
            catch { }
        }

        private void BrowseFiles()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Выбрать файлы",
                Filter = "Все файлы|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in dlg.FileNames)
                if (!Picked.Contains(f)) Picked.Add(f);
            RefreshState();
        }

        private void RebuildFilesList()
        {
            _filesList.SuspendLayout();
            _filesList.Controls.Clear();
            for (int i = 0; i < Picked.Count; i++)
            {
                string p = Picked[i];
                string ext = Path.GetExtension(p).ToLowerInvariant();
                bool isMedia = Array.IndexOf(ImageExt, ext) >= 0 || Array.IndexOf(VideoExt, ext) >= 0;

                var row = new Panel
                {
                    Width = Math.Max(300, _filesPage.ClientSize.Width - 40),
                    Height = 34,
                    BackColor = Color.FromArgb(40, 42, 47),
                    Margin = new Padding(6, 3, 6, 0),
                    Cursor = Cursors.Hand,
                };
                int no = i + 1;
                string name = Path.GetFileName(p);
                string kind = isMedia ? "из галереи" : "файл";
                row.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using var circle = new SolidBrush(Color.FromArgb(30, 143, 225));
                    e.Graphics.FillEllipse(circle, 6, 7, 20, 20);
                    using var f1 = new Font("Segoe UI Semibold", 8f, FontStyle.Bold);
                    using var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    e.Graphics.DrawString(no.ToString(), f1, Brushes.White, new RectangleF(6, 7, 20, 20), sf);
                    using var f2 = new Font("Segoe UI", 9f);
                    using var br = new SolidBrush(Color.FromArgb(220, 222, 225));
                    e.Graphics.DrawString(name, f2, br, 34, 8);
                    using var f3 = new Font("Segoe UI", 7.5f);
                    using var br2 = new SolidBrush(Color.FromArgb(140, 143, 150));
                    var sz = e.Graphics.MeasureString(name, f2);
                    e.Graphics.DrawString(kind + " · нажмите, чтобы убрать", f3, br2, 38 + sz.Width, 11);
                };
                string captured = p;
                row.Click += (s, e) => { Picked.Remove(captured); RefreshState(); };
                _filesList.Controls.Add(row);
            }
            if (Picked.Count == 0)
            {
                _filesList.Controls.Add(new Label
                {
                    Text = "Выбранное появится здесь — и уйдёт вместе с фотографиями, одной пачкой.",
                    AutoSize = true,
                    ForeColor = Color.FromArgb(140, 143, 150),
                    Margin = new Padding(10, 10, 0, 0),
                });
            }
            _filesList.ResumeLayout();
        }

        private void Finish(bool send)
        {
            if (Picked.Count == 0) return;
            SendNow = send;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnClosingGuard(object sender, FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK || Picked.Count == 0) return;
            // Смахнуть окно и потерять отмеченные два десятка снимков обиднее
            // всего — спрашиваем, как это делает Telegram.
            var ans = MessageBox.Show(this,
                $"Закрыть окно вложений? Отмеченные {Picked.Count} шт. не отправятся.",
                "Сбросить выбор", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ans == DialogResult.No) e.Cancel = true;
            else Picked.Clear();
        }

        // ── Плитка ───────────────────────────────────────────────────
        private sealed class Tile : Panel
        {
            public string Path { get; }
            private readonly bool _isVideo;
            private Bitmap _thumb;
            private int _order;

            public event Action<string> ToggleRequested;
            public event Action<string> OpenRequested;

            public Tile(string path, int side)
            {
                Path = path;
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                _isVideo = Array.IndexOf(VideoExt, ext) >= 0;
                Size = new Size(side, side);
                Margin = new Padding(3);
                BackColor = Color.FromArgb(23, 24, 28);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint, true);
            }

            public void SetThumb(Bitmap bmp)
            {
                if (IsDisposed) { bmp?.Dispose(); return; }
                var old = _thumb;
                _thumb = bmp;
                old?.Dispose();
                Invalidate();
            }

            public void SetOrder(int order)
            {
                if (_order == order) return;
                _order = order;
                Invalidate();
            }

            private Rectangle CircleRect => new(Width - 38, 8, 30, 30);

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_thumb != null) g.DrawImage(_thumb, 0, 0, Width, Height);
                else
                {
                    using var br = new SolidBrush(Color.FromArgb(40, 42, 48));
                    g.FillRectangle(br, 0, 0, Width, Height);
                    using var f = new Font("Segoe UI", 7.5f);
                    TextRenderer.DrawText(g, System.IO.Path.GetFileName(Path), f,
                        new Rectangle(4, 4, Width - 8, Height - 8),
                        Color.FromArgb(150, 153, 160),
                        TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter
                        | TextFormatFlags.VerticalCenter);
                }

                if (_isVideo)
                {
                    int s = 46;
                    int cx = Width / 2, cy = Height / 2;
                    using var disc = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                    g.FillEllipse(disc, cx - s / 2, cy - s / 2, s, s);
                    var tri = new[]
                    {
                        new Point(cx - 8, cy - 12),
                        new Point(cx - 8, cy + 12),
                        new Point(cx + 14, cy),
                    };
                    g.FillPolygon(Brushes.White, tri);
                }

                // Кружок выбора — отдельная мишень: нажатие по плитке
                // открывает просмотр, и смешивать эти два действия нельзя.
                var rc = CircleRect;
                using (var br = new SolidBrush(_order > 0
                           ? Color.FromArgb(30, 143, 225) : Color.FromArgb(90, 0, 0, 0)))
                    g.FillEllipse(br, rc);
                using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1.6f))
                    g.DrawEllipse(pen, rc);
                if (_order > 0)
                {
                    using var f = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
                    using var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(_order.ToString(), f, Brushes.White, rc, sf);
                }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left) return;
                if (CircleRect.Contains(e.Location)) ToggleRequested?.Invoke(Path);
                else OpenRequested?.Invoke(Path);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { _thumb?.Dispose(); } catch { } }
                base.Dispose(disposing);
            }
        }
    }
}
