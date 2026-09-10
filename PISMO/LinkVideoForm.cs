using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace PISMO
{
    /// <summary>
    /// Проигрыватель видео по ссылке — отдельным окном, не уходя из переписки.
    ///
    /// Здесь всё проще, чем на телефоне: WebView2 открывает адрес так же, как
    /// это сделала бы вкладка браузера, то есть с настоящим источником. Именно
    /// его отсутствия не хватало телефону — там страницу с рамкой приходится
    /// собирать вручную, иначе YouTube отвечает ошибкой 153.
    ///
    /// Прямой файл Chromium показывает своим встроенным проигрывателем, так что
    /// и для него достаточно просто открыть адрес.
    ///
    /// Чего это не умеет: если служба недоступна или заблокирована, окно
    /// останется пустым — внутри чужая страница, и что с ней, нам не видно. На
    /// этот случай в меню ссылки остаётся «Открыть в браузере».
    /// </summary>
    internal sealed class LinkVideoForm : Form
    {
        private readonly WebView2 _web;
        private readonly string _url;
        private Label _lblStatus;

        internal LinkVideoForm(VideoLinks.Playable playable)
        {
            _url = playable.Url;

            Text = "PISMO — " + (playable.Title ?? "Видео");
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(20, 21, 24);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(420, 260);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            Load += async (s, e) => await InitAsync();
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                await _web.EnsureCoreWebView2Async(await WebViewShared.GetAsync());
                if (IsDisposed) return;

                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                // Всплывающие окна службы (например, «смотреть на сайте»)
                // отправляем в браузер, а не открываем внутри плеера.
                _web.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    MainForm.OpenLink(e.Uri);
                };
                // Без этого неудачная навигация выглядела бы просто пустым
                // чёрным окном — ни ошибки, ни подсказки, что делать.
                _web.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (e.IsSuccess) return;
                    ShowStatus("Не удалось открыть видео (" + e.WebErrorStatus +
                               "). Попробуйте «Открыть в браузере».");
                };

                _web.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть проигрыватель: " + ex.Message, "PISMO");
                Close();
            }
        }

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
                Controls.Add(_lblStatus);
                _lblStatus.BringToFront();
            }
            _lblStatus.Text = text;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Снимаем страницу до закрытия: иначе звук продолжает идти, пока
            // окно доживает свой век в сборщике мусора.
            try { _web.CoreWebView2?.Navigate("about:blank"); } catch { }
            try { _web.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        /// <summary>Открывает окно проигрывателя, если ссылку умеем играть.</summary>
        internal static bool TryPlay(IWin32Window owner, string url)
        {
            var playable = VideoLinks.Of(url);
            if (playable.Kind == VideoLinks.Kind.None) return false;
            try
            {
                var form = new LinkVideoForm(playable);
                if (owner is Form f && !f.IsDisposed) form.Show(f); else form.Show();
                return true;
            }
            catch { return false; }
        }
    }
}
