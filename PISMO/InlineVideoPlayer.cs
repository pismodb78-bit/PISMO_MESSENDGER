using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// Встроенное в пузырь видео. Чтобы не плодить тяжёлые WebView2 (каждый — это
    /// отдельный браузер) и не тормозить чат, по умолчанию показывается лёгкая
    /// «обложка» с кнопкой ▶, а реальный плеер (WebView2: перемотка, громкость,
    /// полный экран) запускается ТОЛЬКО по клику — один на просматриваемое видео.
    /// </summary>
    public sealed class InlineVideoPlayer : Panel
    {
        private readonly byte[] _data;
        private readonly string _fileName;
        private WebView2 _web;
        private string _tempDir;
        private const string Host = "pismo-inline.local";
        private string _safeName;
        private Form _fsForm;
        private bool _playing;

        private Label _lblPlay, _lblName;

        public InlineVideoPlayer(byte[] data, string fileName, int boxW, int boxH)
        {
            _data = data;
            _fileName = fileName ?? "video.mp4";
            Size = new Size(boxW, boxH);
            BackColor = Color.FromArgb(20, 21, 24);
            Cursor = Cursors.Hand;

            // Обложка из реальных контролов (надёжно рисуется сразу, без таймингов
            // owner-draw): крупная ▶ по центру + имя файла снизу.
            _lblPlay = new Label
            {
                Dock = DockStyle.Fill,
                Text = "▶",
                Font = new Font("Segoe UI", Math.Max(18f, Math.Min(boxW, boxH) / 6f), FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(20, 21, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            _lblName = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 18,
                Text = Path.GetFileName(_fileName),
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(210, 210, 210),
                BackColor = Color.FromArgb(30, 31, 34),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            _lblPlay.Click += (s, e) => StartPlayer();
            _lblName.Click += (s, e) => StartPlayer();
            Click += (s, e) => StartPlayer();
            Controls.Add(_lblPlay);
            Controls.Add(_lblName);
        }

        private async void StartPlayer()
        {
            if (_playing) return;
            _playing = true;
            try
            {
                try { _lblPlay?.Dispose(); _lblName?.Dispose(); } catch { }
                _tempDir = Path.Combine(Path.GetTempPath(), "pismo_inline_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
                _safeName = "v" + Path.GetExtension(_fileName);
                File.WriteAllBytes(Path.Combine(_tempDir, _safeName), _data);
                string html =
                    "<!doctype html><html><head><meta charset='utf-8'><style>" +
                    "html,body{margin:0;height:100%;background:#141518;overflow:hidden;}" +
                    "video{width:100%;height:100%;object-fit:contain;background:#141518;outline:none;}" +
                    "</style></head><body>" +
                    $"<video src='https://{Host}/{_safeName}' autoplay playsinline controls " +
                    "controlslist='nodownload' preload='auto'></video>" +
                    // Сообщаем хосту, если видеодорожку декодировать нечем: Chromium в
                    // этом случае молча играет ОДИН ЗВУК и показывает чёрный
                    // прямоугольник (и другой, компактный набор кнопок) — со стороны
                    // выглядит как «сломалось приложение». Признак — videoWidth == 0
                    // при уже прочитанных метаданных.
                    "<script>" +
                    "var v=document.querySelector('video');" +
                    "function say(k){try{window.chrome.webview.postMessage(k);}catch(e){}}" +
                    "v.addEventListener('loadedmetadata',function(){if(!v.videoWidth)say('novideo');});" +
                    "v.addEventListener('error',function(){say('novideo');});" +
                    "</script>" +
                    "</body></html>";
                File.WriteAllText(Path.Combine(_tempDir, "index.html"), html, System.Text.Encoding.UTF8);

                _web = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(_web);
                await _web.EnsureCoreWebView2Async(await WebViewShared.GetAsync());
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Host, _tempDir, CoreWebView2HostResourceAccessKind.Allow);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    string msg = null;
                    try { msg = e.TryGetWebMessageAsString(); } catch { }
                    if (msg == "novideo") { try { ShowCodecNotice(); } catch { } }
                };
                _web.CoreWebView2.ContainsFullScreenElementChanged += OnFullScreenChanged;
                _web.CoreWebView2.Navigate($"https://{Host}/index.html");
            }
            catch { _playing = false; }
        }

        /// <summary>Определяет кодек видеодорожки по контейнеру MP4: в таблице
        /// описаний (stsd) лежит fourcc — «hvc1»/«hev1» у HEVC, «avc1» у H.264.</summary>
        private bool LooksLikeHevc()
        {
            try
            {
                if (_data == null) return false;
                // Ищем по всему буферу: stsd лежит в moov, а он бывает и в конце файла
                // (запись с телефона часто оставляет его там).
                byte[][] tags =
                {
                    System.Text.Encoding.ASCII.GetBytes("hvc1"),
                    System.Text.Encoding.ASCII.GetBytes("hev1")
                };
                foreach (var tag in tags)
                    for (int i = 0; i + tag.Length <= _data.Length; i++)
                    {
                        int k = 0;
                        while (k < tag.Length && _data[i + k] == tag[k]) k++;
                        if (k == tag.Length) return true;
                    }
            }
            catch { }
            return false;
        }

        /// <summary>Поверх плеера — объяснение, почему видно только чёрный экран, и
        /// кнопка открыть файл во внешнем плеере (VLC и подобные HEVC умеют).</summary>
        private void ShowCodecNotice()
        {
            if (IsDisposed || Controls.Contains(_lblCodec)) return;

            bool hevc = LooksLikeHevc();
            _lblCodec = new Label
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(60, 40, 20),
                ForeColor = Color.FromArgb(240, 220, 190),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                Text = hevc
                    ? "Видео записано в HEVC (H.265) — Windows не умеет его показывать без\n" +
                      "«HEVC Video Extensions» из Microsoft Store, поэтому слышен только звук.\n" +
                      "Нажмите здесь, чтобы открыть во внешнем плеере (VLC и т.п.)."
                    : "Этот видеокодек Windows показать не может — слышен только звук.\n" +
                      "Нажмите здесь, чтобы открыть во внешнем плеере.",
                Cursor = Cursors.Hand
            };
            _lblCodec.Click += (s, e) => OpenExternally();
            Controls.Add(_lblCodec);
            _lblCodec.BringToFront();
        }

        /// <summary>Открывает видео системным плеером — он может уметь то, чего не
        /// умеет встроенный в Windows декодер.</summary>
        private void OpenExternally()
        {
            try
            {
                string path = Path.Combine(_tempDir ?? Path.GetTempPath(), _safeName ?? "video.mp4");
                if (!File.Exists(path))
                {
                    path = Path.Combine(Path.GetTempPath(), "pismo_" + Path.GetFileName(_fileName));
                    File.WriteAllBytes(path, _data);
                }
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть видео: " + ex.Message,
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private Label _lblCodec;

        private void OnFullScreenChanged(object sender, object e)
        {
            try
            {
                bool fs = _web?.CoreWebView2 != null && _web.CoreWebView2.ContainsFullScreenElement;
                if (fs && _fsForm == null)
                {
                    _fsForm = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        WindowState = FormWindowState.Maximized,
                        BackColor = Color.Black,
                        ShowInTaskbar = false,
                        TopMost = true,
                        KeyPreview = true
                    };
                    _fsForm.KeyDown += (a, b) => { if (b.KeyCode == Keys.Escape) ExitFullScreen(); };
                    Controls.Remove(_web);
                    _web.Dock = DockStyle.Fill;
                    _fsForm.Controls.Add(_web);
                    _fsForm.Show();
                    _fsForm.Activate();
                }
                else if (!fs) ExitFullScreen();
            }
            catch { }
        }

        private void ExitFullScreen()
        {
            if (_fsForm == null) return;
            var f = _fsForm; _fsForm = null;
            try { f.Controls.Remove(_web); _web.Dock = DockStyle.Fill; Controls.Add(_web); } catch { }
            try { f.Close(); f.Dispose(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { ExitFullScreen(); } catch { }
                try { _web?.Dispose(); } catch { }
                try { if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
