using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// Встроенный в пузырь сообщения видео-проигрыватель (как в Telegram):
    /// видео показывается прямо в чате, автозапуск без звука + зацикливание,
    /// нативные элементы перемотки/громкости. Видео отдаётся через virtual host
    /// из временной папки. Используется в личных, групповых и серверных чатах.
    /// </summary>
    public sealed class InlineVideoPlayer : Panel
    {
        private readonly WebView2 _web;
        private readonly string _tempDir;
        private const string Host = "pismo-inline.local";
        private readonly string _safeName;
        private bool _started;

        public InlineVideoPlayer(byte[] data, string fileName, int boxW, int boxH)
        {
            Size = new Size(boxW, boxH);
            BackColor = Color.FromArgb(20, 21, 24);

            _tempDir = Path.Combine(Path.GetTempPath(), "pismo_inline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _safeName = "v" + Path.GetExtension(fileName ?? ".mp4");
            File.WriteAllBytes(Path.Combine(_tempDir, _safeName), data);

            string html =
                "<!doctype html><html><head><meta charset='utf-8'><style>" +
                "html,body{margin:0;height:100%;background:#141518;overflow:hidden;}" +
                "video{width:100%;height:100%;object-fit:contain;background:#141518;outline:none;}" +
                "</style></head><body>" +
                $"<video src='https://{Host}/{_safeName}' autoplay muted loop playsinline controls " +
                "controlslist='nodownload' preload='auto'></video>" +
                "</body></html>";
            File.WriteAllText(Path.Combine(_tempDir, "index.html"), html, System.Text.Encoding.UTF8);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            // Инициализируем CoreWebView2 лениво — только когда плитка реально показана,
            // чтобы не плодить тяжёлые WebView2 для видео вне видимой области.
            HandleCreated += (s, e) => TryStart();
            VisibleChanged += (s, e) => { if (Visible) TryStart(); };
        }

        private async void TryStart()
        {
            if (_started || !IsHandleCreated) return;
            _started = true;
            try
            {
                await _web.EnsureCoreWebView2Async(null);
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Host, _tempDir, CoreWebView2HostResourceAccessKind.Allow);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.Navigate($"https://{Host}/index.html");
            }
            catch { /* если WebView2 не поднялся — пузырь просто покажет тёмный бокс */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _web?.Dispose(); } catch { }
                try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
