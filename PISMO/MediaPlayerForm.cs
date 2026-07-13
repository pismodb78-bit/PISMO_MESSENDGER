using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// Встроенный проигрыватель видео/музыки на WebView2 (HTML5 &lt;video&gt;/&lt;audio&gt;):
    /// нативные ползунки перемотки и громкости, полноэкранный режим. Файл
    /// раскладывается во временную папку и отдаётся через virtual host.
    /// </summary>
    public sealed class MediaPlayerForm : Form
    {
        private readonly WebView2 _web;
        private readonly string _tempDir;
        private const string Host = "pismo-media.local";

        public MediaPlayerForm(byte[] data, string fileName, bool isVideo)
        {
            Text = "PISMO — " + (fileName ?? (isVideo ? "Видео" : "Музыка"));
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(20, 21, 24);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = isVideo ? new Size(820, 520) : new Size(460, 140);
            MinimumSize = new Size(360, 120);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            _tempDir = Path.Combine(Path.GetTempPath(), "pismo_media_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            string safeName = "media" + Path.GetExtension(fileName ?? "");
            string filePath = Path.Combine(_tempDir, safeName);
            File.WriteAllBytes(filePath, data);

            string tag = isVideo ? "video" : "audio";
            string html =
                "<!doctype html><html><head><meta charset='utf-8'><style>" +
                "html,body{margin:0;height:100%;background:#141518;display:flex;align-items:center;justify-content:center;}" +
                (isVideo ? "video{max-width:100%;max-height:100%;outline:none;}"
                         : "audio{width:92%;}") +
                "</style></head><body>" +
                $"<{tag} src='https://{Host}/{safeName}' controls autoplay></{tag}>" +
                "</body></html>";
            File.WriteAllText(Path.Combine(_tempDir, "index.html"), html, System.Text.Encoding.UTF8);

            Load += async (s, e) => await InitAsync();
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async(await WebViewShared.GetAsync());
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Host, _tempDir, CoreWebView2HostResourceAccessKind.Allow);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.Navigate($"https://{Host}/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть проигрыватель: " + ex.Message, "PISMO");
                Close();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _web.Dispose(); } catch { }
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
            base.OnFormClosed(e);
        }

        // ── Какие расширения проигрываем встроенно (поддержка Chromium) ──
        private static readonly string[] AudioExt = { "mp3", "wav", "ogg", "oga", "m4a", "aac", "flac", "opus", "weba" };
        private static readonly string[] VideoExt = { "mp4", "webm", "m4v", "ogv", "mov" };

        public static bool IsAudio(string ext) => Array.IndexOf(AudioExt, ext) >= 0;
        public static bool IsVideo(string ext) => Array.IndexOf(VideoExt, ext) >= 0;
        public static bool IsMedia(string ext) => IsAudio(ext) || IsVideo(ext);
    }
}
