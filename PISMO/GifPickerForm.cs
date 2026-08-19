using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>Окно поиска гифок Giphy (как кнопка GIF в Discord).</summary>
    public sealed class GifPickerForm : Form
    {
        public event Action<string> GifSelected; // полный URL выбранной гифки

        private readonly TextBox _search;
        private readonly FlowLayoutPanel _grid;
        private readonly System.Windows.Forms.Timer _debounce;
        private readonly Label _status;

        public GifPickerForm()
        {
            Text = "GIF";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(40, 42, 46);
            ClientSize = new Size(470, 520);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _search = new TextBox
            {
                Dock = DockStyle.Top,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(30, 31, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 11f),
                Height = 30,
                PlaceholderText = "Поиск GIF в Giphy…"
            };
            _grid = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(40, 42, 46),
                Padding = new Padding(6)
            };
            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                ForeColor = Color.FromArgb(150, 152, 158),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Популярное"
            };

            Controls.Add(_grid);
            Controls.Add(_status);
            Controls.Add(_search);

            _debounce = new System.Windows.Forms.Timer { Interval = 450 };
            _debounce.Tick += async (s, e) => { _debounce.Stop(); await DoSearch(_search.Text); };
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };

            Load += async (s, e) => await LoadTrending();
        }

        private async System.Threading.Tasks.Task LoadTrending()
        {
            _status.Text = "Популярное…";
            var items = await GiphyClient.TrendingAsync();
            Populate(items);
            _status.Text = items.Count > 0 ? "Популярное" : "Не удалось загрузить (проверьте интернет/лимит ключа)";
        }

        private async System.Threading.Tasks.Task DoSearch(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) { await LoadTrending(); return; }
            _status.Text = "Поиск…";
            var items = await GiphyClient.SearchAsync(q);
            Populate(items);
            _status.Text = items.Count > 0 ? $"Результаты: {q}" : "Ничего не найдено";
        }

        private void Populate(List<GifItem> items)
        {
            // Очищаем старую сетку.
            foreach (Control c in _grid.Controls) { try { (c as PictureBox)?.Image?.Dispose(); } catch { } }
            _grid.Controls.Clear();

            foreach (var it in items)
            {
                var pb = new PictureBox
                {
                    Size = new Size(140, 105),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.FromArgb(30, 31, 34),
                    Margin = new Padding(4),
                    Cursor = Cursors.Hand,
                    WaitOnLoad = false,
                    Tag = it.FullUrl
                };
                pb.Click += (s, e) =>
                {
                    var url = (string)((PictureBox)s).Tag;
                    GifSelected?.Invoke(url);
                    Close();
                };
                _grid.Controls.Add(pb);
                // Грузим превью вручную и показываем СТАТИЧНЫЙ первый кадр.
                // pb.LoadAsync на анимированном GIF включал ImageAnimator в
                // PictureBox → падение GDI+ ("A generic error occurred").
                _ = LoadPreviewStatic(pb, it.PreviewUrl);
            }
        }

        /// <summary>Скачивает превью и кладёт в PictureBox ОДИН (первый) кадр —
        /// без авто-анимации, чтобы не падал ImageAnimator/GDI+.</summary>
        private static async System.Threading.Tasks.Task LoadPreviewStatic(PictureBox pb, string url)
        {
            byte[] data;
            try { data = await GiphyClient.DownloadAsync(url); }
            catch { return; }
            if (data == null || data.Length == 0) return;

            Bitmap firstFrame;
            try
            {
                using var ms = new System.IO.MemoryStream(data);
                using var src = Image.FromStream(ms);
                firstFrame = new Bitmap(src.Width, src.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(firstFrame);
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            catch { return; }

            if (pb.IsDisposed) { firstFrame.Dispose(); return; }
            try
            {
                if (pb.InvokeRequired)
                    pb.BeginInvoke(new Action(() => { if (!pb.IsDisposed) pb.Image = firstFrame; else firstFrame.Dispose(); }));
                else
                    pb.Image = firstFrame;
            }
            catch { firstFrame.Dispose(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _debounce.Stop(); _debounce.Dispose(); } catch { }
            foreach (Control c in _grid.Controls) { try { (c as PictureBox)?.Image?.Dispose(); } catch { } }
            base.OnFormClosed(e);
        }
    }
}
