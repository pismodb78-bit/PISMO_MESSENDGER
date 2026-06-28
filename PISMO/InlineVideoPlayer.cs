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
                    "</body></html>";
                File.WriteAllText(Path.Combine(_tempDir, "index.html"), html, System.Text.Encoding.UTF8);

                _web = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(_web);
                await _web.EnsureCoreWebView2Async(null);
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Host, _tempDir, CoreWebView2HostResourceAccessKind.Allow);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.ContainsFullScreenElementChanged += OnFullScreenChanged;
                _web.CoreWebView2.Navigate($"https://{Host}/index.html");
            }
            catch { _playing = false; }
        }

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
