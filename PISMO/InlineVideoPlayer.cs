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
        private byte[] _data;
        private readonly Func<byte[]> _loader;   // для больших видео: байты берём по клику
        private readonly string _fileName;
        private WebView2 _web;
        private string _tempDir;
        private const string Host = "pismo-inline.local";
        private string _safeName;
        private Form _fsForm;
        private bool _playing;
        private bool _converting, _converted;
        private bool _statusMode;   // в плашке сейчас прогресс, а не финальное сообщение
        private bool _convertFailed;

        private Label _lblPlay, _lblName;

        /// <summary>
        /// Видео, байты которого ещё не загружены: в ленте показывается такая же
        /// обложка, а файл читается из БД по клику. Иначе крупный ролик пришлось
        /// бы тянуть при каждом открытии чата — ради превью, которое, может, и не
        /// станут смотреть.
        /// </summary>
        public InlineVideoPlayer(Func<byte[]> loader, string fileName, int boxW, int boxH)
            : this((byte[])null, fileName, boxW, boxH)
        {
            _loader = loader;
        }

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
            // Если видео в HEVC, показать его без конвертации почти наверняка не
            // выйдет. Начинаем тянуть конвертер сразу, пока человек только
            // смотрит на обложку, — к клику он обычно уже на диске.
            try { if (_data != null && LooksLikeHevc()) VideoTranscoder.Prefetch(); } catch { }

            _lblPlay.Click += (s, e) => StartPlayer();
            _lblName.Click += (s, e) => StartPlayer();
            Click += (s, e) => StartPlayer();
            Controls.Add(_lblPlay);
            Controls.Add(_lblName);
        }

        /// <summary>Страница плеера. Пишется заново после перекодирования — там
        /// зашито имя файла, а оно меняется на сконвертированное.</summary>
        private void WriteHtml()
        {
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
        }

        private async void StartPlayer()
        {
            if (_playing) return;
            _playing = true;
            try
            {
                // Ленивое видео: сначала достаём байты, и только потом всё остальное.
                if (_data == null && _loader != null)
                {
                    if (_lblPlay != null) _lblPlay.Text = "Загрузка…";
                    var loader = _loader;
                    byte[] loaded = null;
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        try { loaded = loader(); } catch { }
                    });
                    if (IsDisposed) return;
                    if (loaded == null || loaded.Length == 0)
                    {
                        if (_lblPlay != null) _lblPlay.Text = "Не удалось загрузить";
                        _playing = false;
                        return;
                    }
                    _data = loaded;
                }

                try { _lblPlay?.Dispose(); _lblName?.Dispose(); } catch { }
                _tempDir = Path.Combine(Path.GetTempPath(), "pismo_inline_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
                _safeName = "v" + Path.GetExtension(_fileName);
                // Если это видео уже перекодировали раньше — сразу берём готовый
                // H.264, не показывая чёрный экран и не гоняя конвертер снова.
                string ready = VideoTranscoder.CachedPath(_data);

                // HEVC системе показать нечем — это известно ЗАРАНЕЕ, по самому
                // файлу. Раньше мы всё равно запускали плеер, он играл один звук
                // на чёрном фоне, и только потом шла конвертация с перезапуском.
                // Теперь конвертируем сразу и открываем уже готовое видео.
                if (ready == null && LooksLikeHevc())
                {
                    _converting = true;
                    try { ready = await VideoTranscoder.ToH264Async(_data, ShowStatus); }
                    catch { }
                    _converting = false;
                    if (IsDisposed) return;
                    if (ready != null) { _converted = true; HideNotice(); }
                    else _convertFailed = true;   // не пробуем второй раз по 'novideo'
                }

                if (ready != null)
                {
                    _safeName = "v264.mp4";
                    File.Copy(ready, Path.Combine(_tempDir, _safeName), true);
                    _converted = true;
                }
                else File.WriteAllBytes(Path.Combine(_tempDir, _safeName), _data);
                WriteHtml();

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
                    if (msg == "novideo") { try { OnNoVideo(); } catch { } }
                };
                _web.CoreWebView2.ContainsFullScreenElementChanged += OnFullScreenChanged;
                _web.CoreWebView2.Navigate($"https://{Host}/index.html");
            }
            catch { _playing = false; }
        }

        /// <summary>Кодек этого видео — HEVC? Определение общее с конвертером.</summary>
        private bool LooksLikeHevc() => VideoTranscoder.LooksLikeHevc(_data);

        /// <summary>
        /// Chromium сообщил, что видеодорожку показать нечем. Ставить кодек из
        /// Store руками не заставляем: молча перегоняем файл в H.264 своим
        /// конвертером и перезапускаем плеер. Плашка нужна только чтобы человек
        /// понимал, почему пару секунд ничего не происходит, и как поступить,
        /// если конвертация всё-таки не удалась.
        /// </summary>
        private async void OnNoVideo()
        {
            if (_converted || _converting) return;
            if (_convertFailed) { ShowCodecNotice(); return; }
            _converting = true;

            ShowStatus("Готовим видео к показу…");
            string h264 = null;
            try { h264 = await VideoTranscoder.ToH264Async(_data, ShowStatus); } catch { }

            if (IsDisposed) return;

            if (h264 == null) { _converting = false; ShowCodecNotice(); return; }

            try
            {
                _safeName = "v264.mp4";
                File.Copy(h264, Path.Combine(_tempDir, _safeName), true);
                _converted = true;
                WriteHtml();
                HideNotice();
                // ?v= — чтобы WebView2 не отдал страницу из кэша: файл тот же,
                // а содержимое уже другое.
                _web?.CoreWebView2?.Navigate($"https://{Host}/index.html?v={Environment.TickCount}");
            }
            catch { ShowCodecNotice(); }
            finally { _converting = false; }
        }

        /// <summary>Короткая строка состояния поверх плеера (без кнопок).</summary>
        private void ShowStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(ShowStatus), text); } catch { } return; }

            // Прогресс приходит часто (на каждый процент) — панель не пересоздаём,
            // только меняем текст, иначе чат заметно моргает.
            if (_pnlCodec != null && !_pnlCodec.IsDisposed && _statusMode && _lblCodec != null)
            {
                _lblCodec.Text = text;
                FitNotice();
                return;
            }

            HideNotice();
            _statusMode = true;
            _pnlCodec = new Panel { Dock = DockStyle.Top, BackColor = Color.FromArgb(40, 44, 52), Height = 24 };
            _lblCodec = new Label
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(40, 44, 52),
                ForeColor = Color.FromArgb(225, 228, 234),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8, 6, 8, 6),
                Text = text,
                AutoSize = false
            };
            // Раздача бывает медленной независимо от канала — даём выход тем, у
            // кого ffmpeg уже есть или кто скачает его сам, быстрее и как удобно.
            _lnkCodec = new Label
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(40, 44, 52),
                ForeColor = Color.FromArgb(150, 205, 255),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Underline),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8, 0, 8, 6),
                Text = "Долго? Указать свой ffmpeg.exe",
                Cursor = Cursors.Hand,
                AutoSize = false
            };
            _lnkCodec.Click += (s, e) => PickFfmpegManually();
            _pnlCodec.Controls.Add(_lnkCodec);
            _pnlCodec.Controls.Add(_lblCodec);
            Controls.Add(_pnlCodec);
            _pnlCodec.BringToFront();
            FitNotice();
            _pnlCodec.Resize += (s, e) => FitNotice();
        }

        private void HideNotice()
        {
            try { if (_pnlCodec != null) { Controls.Remove(_pnlCodec); _pnlCodec.Dispose(); } } catch { }
            _pnlCodec = null; _lblCodec = null; _lnkCodec = null; _lnkManual = null; _lnkLog = null; _statusMode = false;
        }

        /// <summary>Пользователь показывает свой ffmpeg.exe — копируем его туда,
        /// где приложение его ищет, и сразу пробуем сконвертировать снова.</summary>
        private void PickFfmpegManually()
        {
            try
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Выберите ffmpeg.exe",
                    Filter = "ffmpeg.exe|ffmpeg.exe|Программы (*.exe)|*.exe",
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (!VideoTranscoder.UseFfmpegFrom(dlg.FileName))
                {
                    MessageBox.Show("Не удалось использовать этот файл.",
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _converting = false;
                _convertFailed = false;
                OnNoVideo();     // конвертер на месте — повторяем попытку
            }
            catch { }
        }

        /// <summary>Высота плашки — по реально нужной для текущей ширины пузыря.</summary>
        private void FitNotice()
        {
            try
            {
                if (_pnlCodec == null || _pnlCodec.IsDisposed) return;
                int total = 0;
                foreach (var lbl in new[] { _lblCodec, _lnkCodec, _lnkManual, _lnkLog })
                {
                    if (lbl == null || lbl.IsDisposed) continue;
                    int w = Math.Max(60, _pnlCodec.ClientSize.Width - lbl.Padding.Horizontal);
                    int h = TextRenderer.MeasureText(lbl.Text, lbl.Font,
                                new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height;
                    lbl.Height = h + lbl.Padding.Vertical;
                    total += lbl.Height;
                }
                _pnlCodec.Height = total;
            }
            catch { }
        }

        /// <summary>Поверх плеера — объяснение, почему видно только чёрный экран, и
        /// кнопка открыть файл во внешнем плеере (VLC и подобные HEVC умеют).
        /// Запасной вариант: показывается, только если автоконвертация не вышла.</summary>
        private void ShowCodecNotice()
        {
            if (IsDisposed) return;
            HideNotice();

            bool hevc = LooksLikeHevc();
            // БЕЗ жёстких переносов: пузырь бывает узким (~180px), и заранее
            // расставленные «\n» только мешают — текст переносится сам, а высоту
            // мы считаем по факту. С фиксированной высотой хвост обрезался.
            string txt = hevc
                ? "Видео в HEVC (H.265) — не удалось перекодировать его для показа, слышен только звук. Нажмите, чтобы открыть во внешнем плеере."
                : "Этот видеокодек показать не удалось — слышен только звук. Нажмите, чтобы открыть во внешнем плеере.";
            // Причина сбоя прямо в плашке: без неё оставалось только гадать.
            string why = VideoTranscoder.LastError;
            if (!string.IsNullOrWhiteSpace(why)) txt += "  Причина: " + why;

            _pnlCodec = new Panel
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 40, 20),
                Height = 24
            };

            _lblCodec = new Label
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 40, 20),
                ForeColor = Color.FromArgb(240, 220, 190),
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8, 6, 8, 6),
                Text = txt,
                Cursor = Cursors.Hand,
                AutoSize = false
            };
            _lblCodec.Click += (s, e) => OpenExternally();

            // У HEVC есть БЕСПЛАТНАЯ сборка кодека от производителей устройств —
            // тот же декодер, что и в платном «HEVC Video Extensions», но её не
            // видно поиском в Store, только по прямой ссылке на карточку товара.
            if (hevc)
            {
                _lnkCodec = new Label
                {
                    Dock = DockStyle.Top,
                    BackColor = Color.FromArgb(60, 40, 20),
                    ForeColor = Color.FromArgb(150, 205, 255),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Underline),
                    TextAlign = ContentAlignment.TopLeft,
                    Padding = new Padding(8, 0, 8, 6),
                    Text = "Установить бесплатный кодек HEVC (Microsoft Store)",
                    Cursor = Cursors.Hand,
                    AutoSize = false
                };
                _lnkCodec.Click += (s, e) => OpenHevcStorePage();
                _pnlCodec.Controls.Add(_lnkCodec);
            }

            // Свой ffmpeg — самый быстрый выход, если наша раздача не далась.
            _lnkManual = new Label
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 40, 20),
                ForeColor = Color.FromArgb(150, 205, 255),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Underline),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8, 0, 8, 6),
                Text = "Указать свой ffmpeg.exe",
                Cursor = Cursors.Hand,
                AutoSize = false
            };
            _lnkManual.Click += (s, e) => PickFfmpegManually();
            _pnlCodec.Controls.Add(_lnkManual);

            _lnkLog = new Label
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(60, 40, 20),
                ForeColor = Color.FromArgb(150, 205, 255),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Underline),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(8, 0, 8, 6),
                Text = "Открыть лог конвертации",
                Cursor = Cursors.Hand,
                AutoSize = false
            };
            _lnkLog.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        VideoTranscoder.LogPath) { UseShellExecute = true });
                }
                catch { }
            };
            _pnlCodec.Controls.Add(_lnkLog);
            _pnlCodec.Controls.Add(_lblCodec);   // добавлен последним → лежит выше ссылки

            // Высоту считаем по факту (FitNotice) и пересчитываем при изменении
            // размера: пузырь тянется вместе с окном.
            _pnlCodec.Resize += (s, e) => FitNotice();
            Controls.Add(_pnlCodec);
            _pnlCodec.BringToFront();
            FitNotice();   // первый расчёт: ширина уже известна после Add
        }

        /// <summary>Открывает карточку бесплатного HEVC-декодера «from Device
        /// Manufacturer» — по поиску в Store её нет, только по прямой ссылке.
        /// Сначала пробуем протокол Store, при неудаче — сайт в браузере.</summary>
        private void OpenHevcStorePage()
        {
            const string productId = "9n4wgh0z6vhq";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ms-windows-store://pdp/?ProductId=" + productId) { UseShellExecute = true });
            }
            catch
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "https://apps.microsoft.com/detail/" + productId) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось открыть Microsoft Store: " + ex.Message,
                        "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
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

        private Panel _pnlCodec;
        private Label _lblCodec, _lnkCodec, _lnkManual, _lnkLog;

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
