// ============================================================
//  ImageViewerForm — переходы по медиа чата и инструменты
// ============================================================
//
//  Просмотр открывался на ОДНОЙ картинке: чтобы увидеть соседнюю, нужно было
//  закрыть окно, найти её в ленте и открыть заново. В Telegram из просмотра
//  листают стрелками по всем снимкам и роликам переписки, а внизу видно, что
//  это за кадр и чей он. Теперь так же.
//
//  Список медиа собирает тот, кто открывает просмотр (лента переписки или
//  канала): он знает, что сейчас на странице. Байты достаются по требованию
//  и в фоне — вложение может лежать только в базе, и поход за ним на потоке
//  окна заморозил бы просмотр.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>Одно медиа переписки — для переходов в просмотре.</summary>
    internal sealed class MediaRef
    {
        public int MsgId;
        public bool IsVideo;
        public string FileName;
        public string Sender;
        public DateTime When;
    }

    internal sealed partial class ImageViewerForm
    {
        private List<MediaRef> _nav;
        private int _navIdx = -1;
        private Func<MediaRef, byte[]> _navLoader;

        private Panel _btnPrev, _btnNext, _tools;
        private Label _lblInfo;
        private InlineVideoPlayer _videoHost;
        private byte[] _curBytes;
        private string _curName = "image.png";

        /// <summary>
        /// Задать ленту медиа и открыть её элемент.
        /// </summary>
        public void SetNavigation(List<MediaRef> items, int index, Func<MediaRef, byte[]> loader)
        {
            _nav = items;
            _navLoader = loader;
            EnsureChrome();
            GoTo(index);
        }

        private void EnsureChrome()
        {
            if (_btnPrev != null) return;

            _btnPrev = MakeArrow(left: true);
            _btnNext = MakeArrow(left: false);
            _canvas.Controls.Add(_btnPrev);
            _canvas.Controls.Add(_btnNext);

            _lblInfo = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(225, 227, 230),
                BackColor = Color.FromArgb(28, 29, 32),
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(10, 6, 10, 6),
            };
            _canvas.Controls.Add(_lblInfo);

            _tools = new Panel
            {
                Height = 44,
                Width = 212,
                BackColor = Color.FromArgb(28, 29, 32),
            };
            AddTool(_tools, 0, "⤓", "Сохранить как…", SaveCurrent);
            AddTool(_tools, 1, "⟲", "Повернуть", RotateCurrent);
            AddTool(_tools, 2, "⧉", "Копировать", CopyCurrent);
            AddTool(_tools, 3, "✕", "Закрыть", () => Close());
            _canvas.Controls.Add(_tools);

            _canvas.Resize += (s, e) => LayoutChrome();
            LayoutChrome();

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Left) { Step(-1); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { Step(+1); e.Handled = true; }
            };
        }

        private Panel MakeArrow(bool left)
        {
            var p = new Panel { Size = new Size(46, 46), BackColor = Color.Transparent, Cursor = Cursors.Hand };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var br = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillEllipse(br, 0, 0, 45, 45);
                using var pen = new Pen(Color.White, 2.2f);
                int cx = 23, cy = 23, d = 8;
                if (left)
                {
                    g.DrawLine(pen, cx + d / 2, cy - d, cx - d / 2, cy);
                    g.DrawLine(pen, cx - d / 2, cy, cx + d / 2, cy + d);
                }
                else
                {
                    g.DrawLine(pen, cx - d / 2, cy - d, cx + d / 2, cy);
                    g.DrawLine(pen, cx + d / 2, cy, cx - d / 2, cy + d);
                }
            };
            p.Click += (s, e) => Step(left ? -1 : +1);
            return p;
        }

        private static void AddTool(Panel host, int index, string glyph, string hint, Action onClick)
        {
            var b = new Button
            {
                Text = glyph,
                Size = new Size(44, 36),
                Location = new Point(6 + index * 50, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(44, 46, 51),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(b, hint);
            b.Click += (s, e) => { try { onClick(); } catch { } };
            host.Controls.Add(b);
        }

        private void LayoutChrome()
        {
            if (_btnPrev == null) return;
            int h = _canvas.ClientSize.Height, w = _canvas.ClientSize.Width;
            _btnPrev.Location = new Point(14, Math.Max(0, h / 2 - 23));
            _btnNext.Location = new Point(Math.Max(0, w - 60), Math.Max(0, h / 2 - 23));
            _lblInfo.Location = new Point(14, Math.Max(0, h - _lblInfo.Height - 16));
            _tools.Location = new Point(Math.Max(0, (w - _tools.Width) / 2), Math.Max(0, h - _tools.Height - 14));
            _btnPrev.BringToFront();
            _btnNext.BringToFront();
            _lblInfo.BringToFront();
            _tools.BringToFront();
        }

        /// <summary>
        /// Стрелки клавиатуры листают медиа.
        ///
        /// Именно ProcessCmdKey, а не KeyDown: стрелки WinForms сначала
        /// забирает себе на переход между контролами, и обычный обработчик
        /// формы их уже не видит.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_nav != null && _nav.Count > 1)
            {
                if (keyData == Keys.Left) { Step(-1); return true; }
                if (keyData == Keys.Right) { Step(+1); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Step(int delta)
        {
            if (_nav == null || _nav.Count == 0) return;
            int next = _navIdx + delta;
            if (next < 0 || next >= _nav.Count) return;
            GoTo(next);
        }

        private void GoTo(int idx)
        {
            if (_nav == null || idx < 0 || idx >= _nav.Count) return;
            _navIdx = idx;
            var it = _nav[idx];

            _btnPrev.Visible = idx > 0;
            _btnNext.Visible = idx < _nav.Count - 1;
            UpdateInfo(it);

            // Прошлый ролик закрываем всегда: двух дорожек разом быть не должно.
            DropVideo();

            var loader = _navLoader;
            if (loader == null) return;

            // Байты — в фоне: вложение может лежать только в базе.
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] data = null;
                try { data = loader(it); } catch { }
                if (data == null || data.Length == 0 || IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || _navIdx != idx) return;   // успели перелистнуть
                        _curBytes = data;
                        _curName = string.IsNullOrWhiteSpace(it.FileName)
                            ? (it.IsVideo ? "video.mp4" : "image.png") : it.FileName;
                        if (it.IsVideo) ShowVideo(data, _curName);
                        else
                        {
                            var ms = new MemoryStream(data);
                            SetImage(Image.FromStream(ms), ms);
                            LayoutChrome();
                        }
                    }));
                }
                catch { }
            });
        }

        private void UpdateInfo(MediaRef it)
        {
            string kind = it.IsVideo ? "Видео" : "Фото";
            string who = string.IsNullOrWhiteSpace(it.Sender) ? "" : it.Sender + " · ";
            string when = it.When == default ? "" : it.When.ToString("d MMMM, HH:mm",
                new System.Globalization.CultureInfo("ru-RU"));
            _lblInfo.Text = $"{kind} {_navIdx + 1} из {_nav.Count}\n{who}{when}";
            LayoutChrome();
        }

        private void ShowVideo(byte[] data, string name)
        {
            DropVideo();
            try
            {
                _videoHost = new InlineVideoPlayer(data, name,
                    Math.Max(320, _canvas.ClientSize.Width - 40),
                    Math.Max(240, _canvas.ClientSize.Height - 40))
                {
                    Dock = DockStyle.Fill,
                };
                _canvas.Controls.Add(_videoHost);
                _videoHost.SendToBack();
                LayoutChrome();
            }
            catch { }
        }

        private void DropVideo()
        {
            if (_videoHost == null) return;
            try { _canvas.Controls.Remove(_videoHost); _videoHost.Dispose(); } catch { }
            _videoHost = null;
        }

        private void SaveCurrent()
        {
            if (_curBytes == null || _curBytes.Length == 0) return;
            using var dlg = new SaveFileDialog { FileName = _curName };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllBytes(dlg.FileName, _curBytes); } catch { }
        }

        private void RotateCurrent()
        {
            if (_img == null) return;
            try
            {
                _img.RotateFlip(RotateFlipType.Rotate90FlipNone);
                FitToWindow();
                _canvas.Invalidate();
            }
            catch { }
        }

        private void CopyCurrent()
        {
            try { if (_img != null) Clipboard.SetImage(_img); } catch { }
        }
    }
}
