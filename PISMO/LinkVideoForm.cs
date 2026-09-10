using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PISMO
{
    /// <summary>
    /// Проигрыватель видео по ссылке — окном поверх переписки, не уходя из неё.
    ///
    /// ПОЧЕМУ НЕ ПРОСТО ОТКРЫТЬ АДРЕС ВСТРАИВАНИЯ. Так и было сделано сначала,
    /// и YouTube отвечал на это ошибкой 153 «Ошибка настройки видеопроигрывателя».
    /// Причина та же, что и на телефоне: проигрыватель отказывается работать,
    /// когда его открывают САМ ПО СЕБЕ, без страницы-хозяина, — он не видит,
    /// откуда его позвали. Поэтому страницу с рамкой мы собираем сами и
    /// отдаём её с настоящего адреса: SetVirtualHostNameToFolderMapping делает
    /// из папки на диске обычный https-сайт внутри окна, и рамка внутри него
    /// оказывается ровно в том положении, для которого её и предусмотрели.
    ///
    /// Прямой файл (.mp4 и подобные) никакой рамки не требует — его показывает
    /// встроенный проигрыватель Chromium со всем, что нужно: перемотка,
    /// громкость, пауза, полный экран.
    ///
    /// Окно ровно одно на всё приложение: нажатие на другую ссылку
    /// переключает уже открытое, а не разводит десяток окон с ютубом.
    ///
    /// Чего это не умеет: если служба недоступна или ролик запрещён к
    /// встраиванию, внутри рамки будет её собственное сообщение об этом —
    /// что там, нам не видно. На этот случай в шапке есть «Открыть на сайте»
    /// (та же страница целиком, здесь же) и «В браузере».
    /// </summary>
    internal sealed class LinkVideoForm : Form
    {
        /// <summary>
        /// Имя «сайта», с которого отдаётся страница с рамкой. Домен .example
        /// зарезервирован стандартом и не существует ни у кого: перехватить
        /// чужой адрес мы таким образом не можем.
        /// </summary>
        private const string VirtualHost = "player.pismo.example";

        private readonly WebView2 _web;
        private readonly Label _lblTitle;
        private Label _lblStatus;

        private VideoLinks.Playable _playable;
        private string _pageUrl = "";      // исходный адрес страницы (для «Открыть на сайте»)
        private bool _coreReady;

        // Полный экран: запоминаем, к чему возвращаться.
        private bool _isFull;
        private Rectangle _boundsBeforeFull;
        private FormBorderStyle _styleBeforeFull;
        private FormWindowState _stateBeforeFull;
        private readonly Panel _bar;

        /// <summary>Открытое окно — оно одно на приложение.</summary>
        private static LinkVideoForm _open;

        private LinkVideoForm(VideoLinks.Playable playable, string pageUrl)
        {
            _playable = playable;
            _pageUrl = pageUrl ?? "";

            Text = "PISMO — " + Title();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(20, 21, 24);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(420, 260);

            _bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(30, 32, 36),
            };

            _lblTitle = new Label
            {
                Text = Title(),
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(200, 205, 214),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                BackColor = Color.FromArgb(30, 32, 36),
            };
            buttons.Controls.Add(BarButton("В браузере", (s, e) => MainForm.OpenLink(_pageUrl)));
            buttons.Controls.Add(BarButton("Открыть на сайте", (s, e) => OpenSitePage()));

            _bar.Controls.Add(_lblTitle);
            _bar.Controls.Add(buttons);

            _web = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_web);
            Controls.Add(_bar);

            Load += async (s, e) => await InitAsync();
        }

        private string Title() => string.IsNullOrWhiteSpace(_playable.Title) ? "Видео" : _playable.Title;

        private Button BarButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(160, 200, 255),
                BackColor = Color.FromArgb(30, 32, 36),
                Font = new Font("Segoe UI", 8.5f),
                Margin = new Padding(2, 3, 6, 3),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            return b;
        }

        // ── Запуск ───────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async(await WebViewShared.GetAsync());
                if (IsDisposed) return;

                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                // Всплывающие окна службы («смотреть на сайте» и подобное)
                // отправляем в браузер, а не плодим окна поверх переписки.
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    MainForm.OpenLink(e.Uri);
                };

                // Без этого неудачная навигация выглядела бы пустым чёрным
                // окном — ни ошибки, ни подсказки, что делать дальше.
                core.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess) return;
                    ShowStatus("Не удалось открыть видео (" + e.WebErrorStatus +
                               "). Попробуйте «Открыть на сайте» или «В браузере».");
                };

                // Полный экран просит сама страница (кнопка в проигрывателе).
                // Без этого она разворачивалась бы внутри окна, оставляя
                // рамку окна и шапку, — то есть не разворачивалась бы вовсе.
                core.ContainsFullScreenElementChanged += (s, e) =>
                    SetFullScreen(core.ContainsFullScreenElement);

                core.AcceleratorKeyPressed += OnAcceleratorKey;

                // Папка, которую окно выдаёт за сайт. Складывать её рядом с
                // настройками, а не во временные файлы: временные чистят.
                string folder = PlayerFolder();
                Directory.CreateDirectory(folder);
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);

                _coreReady = true;
                StartPlayback();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть проигрыватель: " + ex.Message, "PISMO");
                Close();
            }
        }

        private static string PlayerFolder() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", "player");

        private void StartPlayback()
        {
            if (!_coreReady || IsDisposed) return;
            HideStatus();

            if (_playable.Kind == VideoLinks.Kind.Direct)
            {
                // Файл Chromium показывает своим проигрывателем — с перемоткой,
                // громкостью и полным экраном. Рамка ему не нужна.
                _web.CoreWebView2.Navigate(_playable.Url);
                return;
            }

            try
            {
                string folder = PlayerFolder();
                Directory.CreateDirectory(folder);

                // Имя у каждой страницы своё. Не из любви к меткам времени:
                // при переключении на другое видео окно показало бы прежнюю
                // страницу из своего кеша, а обычная уловка с «?v=...» здесь
                // не работает — папку окно выдаёт за сайт, и адрес с вопросом
                // превращается в поиск несуществующего файла.
                string name = "p" + DateTime.UtcNow.Ticks.ToString("x") + ".html";
                File.WriteAllText(Path.Combine(folder, name), WrapperHtml(_playable.Url));
                CleanOldPages(folder, name);
                _web.CoreWebView2.Navigate($"https://{VirtualHost}/{name}");
            }
            catch (Exception ex)
            {
                ShowStatus("Не удалось подготовить проигрыватель: " + ex.Message);
            }
        }

        /// <summary>Прошлые страницы не нужны — папка не должна расти вечно.</summary>
        private static void CleanOldPages(string folder, string keep)
        {
            try
            {
                foreach (var f in Directory.GetFiles(folder, "p*.html"))
                {
                    if (string.Equals(Path.GetFileName(f), keep, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Ту, что открыта прямо сейчас, система не отдаст — и не надо.
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Страница-хозяин для рамки проигрывателя.
        ///
        /// ПРО referrerpolicy — это не украшение, а вторая половина лечения от
        /// ошибки 153. С конца 2025 года YouTube требует, чтобы страница,
        /// вставившая проигрыватель, называла себя заголовком Referer; когда
        /// его нет или он вырезан, проигрыватель отказывается настраиваться.
        /// strict-origin-when-cross-origin передаёт только домен и ничего
        /// сверх него — этого хватает, а адрес страницы наружу не уходит.
        /// Правило продублировано в head на случай, если атрибут у рамки
        /// потеряется при разборе.
        /// </summary>
        private static string WrapperHtml(string embedUrl) =>
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">" +
            "<title>PISMO</title><style>" +
            "html,body{margin:0;height:100%;background:#000;overflow:hidden}" +
            "iframe{display:block;width:100%;height:100%;border:0}" +
            "</style></head><body>" +
            "<iframe src=\"" + System.Net.WebUtility.HtmlEncode(embedUrl) + "\" " +
            "referrerpolicy=\"strict-origin-when-cross-origin\" " +
            "allow=\"autoplay; encrypted-media; fullscreen; picture-in-picture\" " +
            "allowfullscreen></iframe></body></html>";

        /// <summary>Та же ссылка, но страницей службы целиком — здесь же, в окне.</summary>
        private void OpenSitePage()
        {
            if (!_coreReady || string.IsNullOrWhiteSpace(_pageUrl)) return;
            HideStatus();
            SetFullScreen(false);
            try { _web.CoreWebView2.Navigate(_pageUrl); } catch { }
        }

        // ── Полный экран ─────────────────────────────────────────────────

        private void SetFullScreen(bool on)
        {
            if (IsDisposed || on == _isFull) return;
            _isFull = on;
            if (on)
            {
                _boundsBeforeFull = Bounds;
                _styleBeforeFull = FormBorderStyle;
                _stateBeforeFull = WindowState;
                _bar.Visible = false;
                // Сначала снимаем рамку и «развёрнутость»: развёрнутое окно
                // размер не меняет, и панель задач осталась бы поверх видео.
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
            }
            else
            {
                FormBorderStyle = _styleBeforeFull;
                WindowState = _stateBeforeFull;
                if (_stateBeforeFull == FormWindowState.Normal) Bounds = _boundsBeforeFull;
                _bar.Visible = true;
            }
        }

        private void OnAcceleratorKey(object sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown) return;
            var key = (Keys)e.VirtualKey;
            if (key == Keys.Escape)
            {
                // В полном экране Esc — дело страницы: она из него и выходит.
                if (_isFull) return;
                e.Handled = true;
                BeginInvoke(new Action(Close));
            }
            else if (key == Keys.F11)
            {
                e.Handled = true;
                BeginInvoke(new Action(() => SetFullScreen(!_isFull)));
            }
        }

        // ── Сообщение об ошибке ──────────────────────────────────────────

        private void ShowStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(ShowStatus), text); } catch { } return; }
            if (_lblStatus == null)
            {
                _lblStatus = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 46,
                    BackColor = Color.FromArgb(40, 44, 52),
                    ForeColor = Color.FromArgb(225, 228, 234),
                    Font = new Font("Segoe UI", 9.5f),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                // Добавляем последним намеренно: WinForms прижимает к краям
                // с конца списка, поэтому последний добавленный и окажется
                // самым верхним. BringToFront сделал бы ровно наоборот —
                // полоса получила бы нулевую высоту под растянутым окном.
                Controls.Add(_lblStatus);
            }
            _lblStatus.Text = text;
            _lblStatus.Visible = true;
        }

        private void HideStatus()
        {
            if (_lblStatus != null) _lblStatus.Visible = false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Снимаем страницу до закрытия: иначе звук продолжает идти, пока
            // окно доживает свой век в сборщике мусора.
            try { _web.CoreWebView2?.Navigate("about:blank"); } catch { }
            try { _web.Dispose(); } catch { }
            if (_open == this) _open = null;
            base.OnFormClosed(e);
        }

        // ── Вход снаружи ─────────────────────────────────────────────────

        /// <summary>Показывает ссылку в проигрывателе. false — играть нечего.</summary>
        internal static bool TryPlay(IWin32Window owner, string url)
        {
            var playable = VideoLinks.Of(url);
            if (playable.Kind == VideoLinks.Kind.None) return false;
            try
            {
                if (_open != null && !_open.IsDisposed)
                {
                    // Второе окно с тем же ютубом никому не нужно: показываем
                    // новое видео в уже открытом.
                    _open.PlayAnother(playable, url);
                    return true;
                }

                var form = new LinkVideoForm(playable, url);
                _open = form;
                if (owner is Form f && !f.IsDisposed) form.Show(f); else form.Show();
                return true;
            }
            catch { return false; }
        }

        private void PlayAnother(VideoLinks.Playable playable, string pageUrl)
        {
            _playable = playable;
            _pageUrl = pageUrl ?? "";
            Text = "PISMO — " + Title();
            _lblTitle.Text = Title();
            SetFullScreen(false);
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            // Пока ядро не готово, играть нечего: InitAsync сам вызовет
            // StartPlayback, и уже с новой ссылкой.
            StartPlayback();
            try { Activate(); } catch { }
        }
    }
}
