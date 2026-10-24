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
    /// <summary>Что просмотр умеет делать с показанным — задаёт лента.</summary>
    internal sealed class MediaViewerActions
    {
        public Action<int> ShowInChat;
        public Action<int> Reply;
        public Action<int> Delete;
        /// <summary>Отправить нарисованное новым снимком в ту же переписку.</summary>
        public Action<byte[]> SendEdited;
    }

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
        private MediaViewerActions _actions;
        private double _speed = 1.0;
        private bool _looping;

        private Panel _btnPrev, _btnNext, _tools;
        private SpinPanel _spinner;
        private Label _lblInfo;
        private InlineVideoPlayer _videoHost;
        private byte[] _curBytes;
        private string _curName = "image.png";

        /// <summary>
        /// Задать ленту медиа и открыть её элемент.
        /// </summary>
        public void SetNavigation(List<MediaRef> items, int index, Func<MediaRef, byte[]> loader,
                                  MediaViewerActions actions = null)
        {
            _nav = items;
            _navLoader = loader;
            if (actions != null) _actions = actions;
            EnsureChrome();
            GoTo(index);
        }

        /// <summary>
        /// Подменить ленту на ПОЛНУЮ, не трогая показанное.
        ///
        /// Окно открывается мгновенно — по тому, что уже на странице
        /// переписки, — а полный список соседей приезжает следом, из базы.
        /// Показанный кадр остаётся на месте, меняются только соседи и
        /// счётчик.
        /// </summary>
        public void ExtendNavigation(List<MediaRef> items, int index, Func<MediaRef, byte[]> loader)
        {
            if (items == null || items.Count == 0) return;
            if (index < 0 || index >= items.Count) return;
            // Открыт не тот кадр — значит человек уже перелистнул; не мешаем.
            var cur = (_nav != null && _navIdx >= 0 && _navIdx < _nav.Count) ? _nav[_navIdx] : null;
            if (cur != null && items[index].MsgId != cur.MsgId) return;

            _nav = items;
            _navIdx = index;
            if (loader != null) _navLoader = loader;
            EnsureChrome();
            _btnPrev.Visible = index > 0;
            _btnNext.Visible = index < items.Count - 1;
            UpdateInfo(items[index]);
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
                Width = 262,
                BackColor = Color.FromArgb(28, 29, 32),
            };
            AddTool(_tools, 0, "⤓", "Сохранить как…", SaveCurrent);
            AddTool(_tools, 1, "⟲", "Повернуть", RotateCurrent);
            AddTool(_tools, 2, "⧉", "Копировать", CopyCurrent);
            AddTool(_tools, 3, "⋮", "Ещё", ShowMoreMenu);
            AddTool(_tools, 4, "✕", "Закрыть", () => Close());
            _canvas.Controls.Add(_tools);

            // Кружок ожидания: вложение может лежать только на сервере, и
            // пока оно едет, в окне не должно быть пустоты. Без процентов —
            // доля тут ничего не решает, а мелькающие числа только мешают.
            _spinner = new SpinPanel { Visible = false };
            _canvas.Controls.Add(_spinner);

            _canvas.Resize += (s, e) =>
            {
                LayoutChrome();
                if (_videoHost != null && !_videoHost.IsDisposed) _videoHost.Bounds = VideoBox();
            };
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
            if (_spinner != null)
                _spinner.Location = new Point(Math.Max(0, (w - _spinner.Width) / 2),
                                              Math.Max(0, (h - _spinner.Height) / 2));
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

            // Пока вложение едет — кружок по центру. Если оно уже в кеше,
            // загрузчик вернётся мгновенно и кружок мелькнуть не успеет.
            ShowSpinner(true);

            // Байты — в фоне: вложение может лежать только в базе.
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[] data = null;
                try { data = loader(it); } catch { }
                if (data == null || data.Length == 0 || IsDisposed)
                {
                    try { BeginInvoke(new Action(() => ShowSpinner(false))); } catch { }
                    return;
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || _navIdx != idx) return;   // успели перелистнуть
                        ShowSpinner(false);
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

        private void ShowSpinner(bool on)
        {
            if (_spinner == null || _spinner.IsDisposed) return;
            if (_spinner.Visible == on) return;
            _spinner.Visible = on;
            if (on) _spinner.BringToFront();
        }

        /// <summary>Кружок ожидания — крутится, пока вложение едет с сервера.</summary>
        private sealed class SpinPanel : Panel
        {
            private readonly System.Windows.Forms.Timer _t;
            private float _angle;

            public SpinPanel()
            {
                Size = new Size(64, 64);
                BackColor = Color.Transparent;
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                _t = new System.Windows.Forms.Timer { Interval = 33 };
                _t.Tick += (s, e) => { _angle = (_angle + 14f) % 360f; Invalidate(); };
                VisibleChanged += (s, e) => { if (Visible) _t.Start(); else _t.Stop(); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var back = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                    g.FillEllipse(back, 0, 0, Width - 1, Height - 1);
                using var pen = new Pen(Color.FromArgb(235, 255, 255, 255), 4f);
                g.DrawArc(pen, 12, 12, Width - 25, Height - 25, _angle, 100);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { _t.Stop(); _t.Dispose(); } catch { } }
                base.Dispose(disposing);
            }
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
                // Видео НЕ растягиваем на всё окно: страница плеера живёт в
                // отдельном окне Chromium, и оно рисуется поверх любых наших
                // кнопок, сколько их ни поднимай вперёд. Поэтому оставляем
                // поля — слева и справа под стрелки, снизу под панель
                // инструментов: тогда и стрелки видны, и «⋮» со скоростью
                // нажимается. Раньше они были, но под роликом — и со стороны
                // это выглядело как «у видео переходов нет».
                var box = VideoBox();
                _videoHost = new InlineVideoPlayer(data, name, box.Width, box.Height)
                {
                    InlinePage = false,         // ⛶ = настоящий полный экран
                    Bounds = box,
                };
                // Стрелки и Esc приходят из самой страницы: пока курсор в ней,
                // окно клавиш не видит.
                _videoHost.NavRequested = d => { try { Step(d); } catch { } };
                _videoHost.CloseRequested = () => { try { Close(); } catch { } };
                _canvas.Controls.Add(_videoHost);
                _videoHost.SendToBack();
                // Выбранные скорость и «по кругу» переживают переход к
                // следующему ролику — как в плеере Telegram.
                _videoHost.SetSpeed(_speed);
                _videoHost.SetLooping(_looping);
                LayoutChrome();
            }
            catch { }
        }

        /// <summary>Место под ролик: поля под стрелки по бокам и панель снизу.</summary>
        private Rectangle VideoBox()
        {
            int w = _canvas.ClientSize.Width, h = _canvas.ClientSize.Height;
            int side = w > 420 ? 74 : 0;          // в узком окне стрелки не влезут всё равно
            int bottom = h > 360 ? 66 : 0;
            return new Rectangle(side, 8,
                                 Math.Max(160, w - side * 2),
                                 Math.Max(120, h - bottom - 8));
        }

        private void DropVideo()
        {
            if (_videoHost == null) return;
            try { _canvas.Controls.Remove(_videoHost); _videoHost.Dispose(); } catch { }
            _videoHost = null;
        }

        /// <summary>
        /// Меню «ещё» — как в Telegram: действия над показанным и, для
        /// роликов, скорость с «по кругу».
        /// </summary>
        private void ShowMoreMenu()
        {
            var it = (_nav != null && _navIdx >= 0 && _navIdx < _nav.Count) ? _nav[_navIdx] : null;
            if (it == null) return;

            var menu = new ContextMenuStrip { ShowImageMargin = false };

            menu.Items.Add("Сохранить как…", null, (s, e) => SaveCurrent());
            menu.Items.Add("Копировать", null, (s, e) => CopyCurrent());

            if (!it.IsVideo && _actions?.SendEdited != null)
                menu.Items.Add("Рисовать…", null, (s, e) => DrawOnCurrent());

            if (it.IsVideo)
            {
                menu.Items.Add(new ToolStripSeparator());

                var speed = new ToolStripMenuItem("Скорость · " + SpeedLabel(_speed));
                foreach (var v in new[] { 0.2, 0.5, 1.0, 1.5, 2.0 })
                {
                    double captured = v;
                    var item = new ToolStripMenuItem(SpeedLabel(v) + "   " + SpeedName(v), null,
                        (s, e) => ApplySpeed(captured))
                    { Checked = Math.Abs(_speed - v) < 0.01 };
                    speed.DropDownItems.Add(item);
                }
                menu.Items.Add(speed);

                menu.Items.Add(new ToolStripMenuItem(_looping ? "Не зацикливать" : "Зациклить", null,
                    (s, e) => ApplyLooping(!_looping)));
                menu.Items.Add("Открыть в…", null, (s, e) => OpenExternally());
            }

            if (_actions != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                int id = it.MsgId;
                if (_actions.ShowInChat != null)
                    menu.Items.Add("Показать в чате", null, (s, e) => { Close(); _actions.ShowInChat(id); });
                if (_actions.Reply != null)
                    menu.Items.Add("Ответить", null, (s, e) => { Close(); _actions.Reply(id); });
                if (_actions.Delete != null)
                    menu.Items.Add("Удалить", null, (s, e) => { Close(); _actions.Delete(id); });
            }

            menu.Show(Cursor.Position);
        }

        private static string SpeedLabel(double v) =>
            Math.Abs(v - Math.Round(v)) < 0.01 ? ((int)Math.Round(v)) + "x" : v.ToString("0.#") + "x";

        private static string SpeedName(double v) => v switch
        {
            < 0.4 => "очень медленно",
            < 0.9 => "медленно",
            < 1.1 => "обычно",
            < 1.9 => "быстро",
            _ => "очень быстро",
        };

        private void ApplySpeed(double v)
        {
            _speed = v;
            try { _videoHost?.SetSpeed(v); } catch { }
        }

        private void ApplyLooping(bool on)
        {
            _looping = on;
            try { _videoHost?.SetLooping(on); } catch { }
        }

        /// <summary>Отдать ролик системному плееру.</summary>
        private void OpenExternally()
        {
            if (_curBytes == null || _curBytes.Length == 0) return;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "PISMO_view");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, _curName);
                File.WriteAllBytes(path, _curBytes);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Рисование поверх снимка — кисть, надпись, наклейки.</summary>
        private void DrawOnCurrent()
        {
            if (_curBytes == null || _curBytes.Length == 0) return;
            try
            {
                using var ed = new PhotoEditorForm(_curBytes);
                if (ed.ShowDialog(this) != DialogResult.OK) return;
                var result = ed.Result;
                if (result == null || result.Length == 0) return;
                _actions?.SendEdited?.Invoke(result);
                // Снимок прикреплён к полю ввода — закрываем просмотр, иначе
                // поля с дописанным текстом за ним не видно.
                Close();
            }
            catch { }
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
