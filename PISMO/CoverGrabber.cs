// ============================================================
//  CoverGrabber — обложки старых роликов, не дожидаясь запуска
// ============================================================
//
//  Новые ролики уходят с кадром сразу (см. VideoCover). У старых его нет, и
//  раньше обложка появлялась, только когда ролик кто-то запускал. Это не то,
//  что видно в Telegram Desktop: там превью есть у всего.
//
//  Здесь кадр снимается В ФОНЕ, сам:
//    • ролик уже лежит в кеше — берём его оттуда, ничего не качая;
//    • не лежит, но небольшой (до AutoDownloadLimit) — докачиваем, как
//      Telegram Desktop докачивает ролики до заданного размера;
//    • крупный и не скачанный — не трогаем: тянуть сотни мегабайт ради
//      картинки нельзя.
//
//  КАДР СНИМАЕТ ТОТ ЖЕ ДВИЖОК, ЧТО ИГРАЕТ РОЛИК. Миниатюры Проводника не
//  годятся: снятое телефоном в HEVC без расширения кодека он не читает, а
//  плеер приложения такие ролики показывает. Поэтому — невидимый WebView2 за
//  краем окна (ровно так живёт транспорт звонка, см. WebRtcTransport): он
//  открывает файл, перематывает на начало, рисует кадр на canvas и отдаёт
//  JPEG. Ролики обрабатываются по одному, чтобы не грузить ни диск, ни
//  процессор пачкой.
//
//  РОЛИКИ НА ФАЙЛОВОМ СЕРВИСЕ НЕ КАЧАЕМ ВОВСЕ, какого бы размера они ни
//  были: сервис отдаёт файл кусками (Range), и плеер читает только заголовок
//  ролика и данные вокруг нужного кадра — сотни килобайт. Прямо со ссылки
//  кадр снять нельзя: сервис не шлёт CORS, и холст с чужим видео «портится».
//  Поэтому страница и ролик отдаются движку с одного своего адреса
//  (RemoteHost), а куски ролика мы сами берём у сервиса по мегабайту.
//
//  Готовый кадр кладётся в кеш картинок и дописывается в саму строку
//  сообщения (VideoCover.Share) — дальше его видят ВСЕ клиенты, в том числе
//  телефон, — и сразу ставится обложкой в плеер, который его ждал.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PISMO
{
    internal static class CoverGrabber
    {
        /// <summary>До какого размера ролик докачиваем ради обложки.</summary>
        private const long AutoDownloadLimit = 25L * 1024 * 1024;

        /// <summary>Сколько ждать кадр с одного ролика, прежде чем сдаться.</summary>
        private const int FrameTimeoutMs = 20000;

        private const string Host = "pismo-cover.local";

        /// <summary>Свой адрес для роликов с файлового сервиса — страница и куски с одного места.</summary>
        private const string RemoteHost = "pismo-cover-remote.local";

        /// <summary>По сколько байт отдаём ролик движку за один запрос.</summary>
        private const int ChunkBytes = 1 << 20;

        /// <summary>Сколько ждём кадр с ролика на сервисе: куски едут по сети.</summary>
        private const int RemoteTimeoutMs = 30000;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>Ролик, который сейчас разбирается удалённо: адрес на сервисе и страница.</summary>
        private static string _remoteUrl, _remotePage, _remoteName;

        private sealed class Job
        {
            public string Key;
            public string Table;
            public int MsgId;
            public string FileName;
            public string CacheKind;
            public Func<byte[]> Loader;
            public bool Cached;
            public long Size;
        }

        private static readonly Queue<Job> _queue = new();
        /// <summary>Что уже пробовали в этой сессии — второй раз не берёмся.</summary>
        private static readonly HashSet<string> _tried = new();
        /// <summary>Плееры, которые ждут обложку своего ролика.</summary>
        private static readonly Dictionary<string, List<WeakReference<InlineVideoPlayer>>> _waiters = new();

        /// <summary>Обложка ролика готова — плитки альбомов перерисовываются.</summary>
        public static event Action<int> CoverReady;

        private static WebView2 _web;
        /// <summary>Сколько раз не удалось поднять движок — после двух сдаёмся до перезапуска.</summary>
        private static int _webFailures;
        private static Task<bool> _webReady;
        private static string _dir;
        private static bool _busy;
        private static TaskCompletionSource<byte[]> _frame;

        /// <summary>
        /// Попросить обложку для ролика без неё. Звать с потока интерфейса.
        /// </summary>
        /// <param name="player">Плеер в ленте — ему обложка поставится сразу.</param>
        /// <param name="table">Таблица сообщения: messages / group_messages / server_messages.</param>
        /// <param name="cacheKind">Ключ кеша картинок: «img» в переписке, «simg» в каналах.</param>
        /// <param name="cached">Лежит ли сам ролик уже в кеше.</param>
        /// <param name="size">Размер ролика в байтах; 0 — неизвестен.</param>
        /// <param name="loader">Как достать байты ролика (из кеша или с сервера).</param>
        public static void Request(InlineVideoPlayer player, string table, int msgId, string fileName,
                                   string cacheKind, bool cached, long size, Func<byte[]> loader)
        {
            if (msgId <= 0 || loader == null || string.IsNullOrEmpty(table)) return;
            // Очередь и невидимый движок живут на потоке интерфейса.
            var ui = MainForm.Current;
            if (ui != null && !ui.IsDisposed && ui.InvokeRequired)
            {
                try { ui.BeginInvoke(new Action(() => Request(player, table, msgId, fileName, cacheKind, cached, size, loader))); }
                catch { }
                return;
            }
            string key = table + ":" + msgId;
            if (player != null)
            {
                if (!_waiters.TryGetValue(key, out var list))
                    _waiters[key] = list = new List<WeakReference<InlineVideoPlayer>>();
                list.Add(new WeakReference<InlineVideoPlayer>(player));
            }
            if (!_tried.Add(key)) return;

            _queue.Enqueue(new Job
            {
                Key = key, Table = table, MsgId = msgId, FileName = fileName ?? "video.mp4",
                CacheKind = cacheKind ?? "img", Loader = loader,
                Cached = cached, Size = size,
            });
            _ = PumpAsync();
        }

        private static async Task PumpAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                while (_queue.Count > 0)
                {
                    var job = _queue.Dequeue();
                    byte[] jpeg = null;
                    try { jpeg = await GrabAsync(job); } catch { jpeg = null; }
                    if (jpeg is { Length: > 0 }) Deliver(job, jpeg);
                    else _waiters.Remove(job.Key);
                }
            }
            finally { _busy = false; }
        }

        private static void Deliver(Job job, byte[] jpeg)
        {
            // В кеш и в строку сообщения — дальше обложку видят все.
            VideoCover.Share(job.Table, job.MsgId, jpeg, job.CacheKind);
            try { CoverReady?.Invoke(job.MsgId); } catch { }

            // И сразу в плееры, которые её ждали.
            if (_waiters.TryGetValue(job.Key, out var list))
            {
                foreach (var w in list)
                    if (w.TryGetTarget(out var p) && !p.IsDisposed && p.Poster == null)
                    {
                        try { p.Poster = jpeg; } catch { }
                    }
                _waiters.Remove(job.Key);
            }
        }

        private static async Task<byte[]> GrabAsync(Job job)
        {
            if (!job.Cached)
            {
                // Ролик на файловом сервисе — кадр по ссылке, без скачивания.
                string url = await Task.Run(() =>
                {
                    try
                    {
                        using var conn = DBHelper.OpenConnection();
                        var u = FileService.UrlOfMessage(conn, job.Table, job.MsgId);
                        return string.IsNullOrWhiteSpace(u) ? null : FileService.Resolve(u);
                    }
                    catch { return null; }
                });
                if (url != null) return await GrabRemoteAsync(job, url);
                // Лежит в базе целиком: докачиваем только небольшие.
                if (job.Size <= 0 || job.Size > AutoDownloadLimit) return null;
            }

            // Байты — в фоне: их может понадобиться докачать.
            byte[] data = await Task.Run(() => { try { return job.Loader(); } catch { return null; } });
            if (data == null || data.Length == 0) return null;

            if (!await EnsureWebAsync()) return null;

            string ext = Path.GetExtension(job.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            string video = "v" + job.MsgId + ext.ToLowerInvariant();
            string page = "p" + job.MsgId + ".html";
            try
            {
                await Task.Run(() =>
                {
                    File.WriteAllBytes(Path.Combine(_dir, video), data);
                    File.WriteAllText(Path.Combine(_dir, page), PageFor(video), System.Text.Encoding.UTF8);
                });
            }
            catch { return null; }

            _frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _web.CoreWebView2.Navigate($"https://{Host}/{page}"); }
            catch { return null; }

            var done = await Task.WhenAny(_frame.Task, Task.Delay(FrameTimeoutMs));
            byte[] jpeg = done == _frame.Task ? _frame.Task.Result : null;

            // Уходим со страницы и убираем за собой: файл ролика держать незачем.
            try { _web.CoreWebView2.Navigate("about:blank"); } catch { }
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                try { File.Delete(Path.Combine(_dir, video)); } catch { }
                try { File.Delete(Path.Combine(_dir, page)); } catch { }
            });
            return jpeg;
        }

        /// <summary>Кадр ролика с файлового сервиса — через свой адрес и куски по мегабайту.</summary>
        private static async Task<byte[]> GrabRemoteAsync(Job job, string url)
        {
            if (!await EnsureWebAsync()) return null;

            string ext = Path.GetExtension(job.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            _remoteUrl = url;
            _remoteName = "v" + job.MsgId + ext.ToLowerInvariant();
            _remotePage = "p" + job.MsgId + ".html";

            _frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _web.CoreWebView2.Navigate($"https://{RemoteHost}/{_remotePage}"); }
            catch { _remoteUrl = null; return null; }

            var done = await Task.WhenAny(_frame.Task, Task.Delay(RemoteTimeoutMs));
            byte[] jpeg = done == _frame.Task ? _frame.Task.Result : null;

            // Уходим со страницы — движок перестаёт просить куски.
            try { _web.CoreWebView2.Navigate("about:blank"); } catch { }
            _remoteUrl = null; _remotePage = null; _remoteName = null;
            return jpeg;
        }

        private static string MimeOf(string name)
        {
            switch (Path.GetExtension(name ?? "").ToLowerInvariant())
            {
                case ".webm": return "video/webm";
                case ".ogv": return "video/ogg";
                case ".mkv": return "video/x-matroska";
                // mov, m4v, 3gp — тот же контейнер ISO, Chromium их так и читает.
                default: return "video/mp4";
            }
        }

        /// <summary>
        /// Запросы движка к своему адресу: страница — из памяти, ролик —
        /// куском у файлового сервиса. Ответ готовим в фоне: на потоке окна
        /// сетевое чтение заморозило бы интерфейс.
        /// </summary>
        private static void OnRemoteRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string uri;
            try { uri = e.Request.Uri; } catch { return; }
            if (uri == null || !uri.StartsWith("https://" + RemoteHost + "/", StringComparison.OrdinalIgnoreCase)) return;
            var core = _web?.CoreWebView2;
            if (core == null) return;
            var env = core.Environment;
            string path = uri.Substring(("https://" + RemoteHost + "/").Length);
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            string real = _remoteUrl, page = _remotePage, name = _remoteName;
            if (real == null || page == null || name == null)
            {
                try { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); } catch { }
                return;
            }

            if (string.Equals(path, page, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var html = System.Text.Encoding.UTF8.GetBytes(PageFor(name, remote: true));
                    e.Response = env.CreateWebResourceResponse(new MemoryStream(html), 200, "OK",
                        "Content-Type: text/html; charset=utf-8");
                }
                catch { }
                return;
            }
            if (!string.Equals(path, name, StringComparison.OrdinalIgnoreCase))
            {
                try { e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", ""); } catch { }
                return;
            }

            // С какого байта просит движок: «bytes=123-» или «bytes=123-456».
            long from = 0;
            try
            {
                string range = e.Request.Headers.GetHeader("Range");
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range.Substring(6);
                    int dash = spec.IndexOf('-');
                    if (dash > 0) long.TryParse(spec.Substring(0, dash), out from);
                }
            }
            catch { }

            CoreWebView2Deferral deferral;
            try { deferral = e.GetDeferral(); } catch { return; }
            var ui = System.Threading.SynchronizationContext.Current;
            string mime = MimeOf(name);
            _ = Task.Run(async () =>
            {
                int status = 502; string reason = "Bad Gateway"; byte[] body = null; string headers = "";
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, real);
                    req.Headers.Range = new RangeHeaderValue(from, from + ChunkBytes - 1);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)resp.StatusCode == 206)
                    {
                        body = await resp.Content.ReadAsByteArrayAsync();
                        var cr = resp.Content.Headers.ContentRange;
                        long a = cr?.From ?? from, len = cr?.Length ?? 0;
                        long b = a + body.Length - 1;
                        status = 206; reason = "Partial Content";
                        headers = $"Content-Type: {mime}\r\nAccept-Ranges: bytes\r\n" +
                                  $"Content-Range: bytes {a}-{b}/{(len > 0 ? len.ToString() : "*")}\r\n" +
                                  $"Content-Length: {body.Length}";
                    }
                    else if (resp.IsSuccessStatusCode)
                    {
                        // Сервис «кусков» не знает — целый файл ради кадра не тянем.
                        status = 416; reason = "Range Not Satisfiable";
                    }
                    else { status = (int)resp.StatusCode; reason = resp.ReasonPhrase ?? "Error"; }
                }
                catch { body = null; }

                void Reply()
                {
                    try
                    {
                        e.Response = env.CreateWebResourceResponse(
                            body != null ? new MemoryStream(body) : null, status, reason, headers);
                    }
                    catch { }
                    finally { try { deferral.Complete(); } catch { } }
                }
                if (ui != null) ui.Post(_ => Reply(), null); else Reply();
            });
        }

        /// <summary>Невидимый движок за краем главного окна — один на приложение.</summary>
        private static Task<bool> EnsureWebAsync()
        {
            if (_webReady != null && !(_webReady.IsCompleted && !_webReady.Result)) return _webReady;
            // Движок не поднялся дважды — не мучаем систему до перезапуска:
            // обложки всё равно снимет плеер, когда ролик запустят.
            if (_webFailures >= 2) return Task.FromResult(false);
            if (_web != null)
            {
                try { _web.Parent?.Controls.Remove(_web); _web.Dispose(); } catch { }
                _web = null;
            }
            _webReady = CreateWebAsync();
            _webReady.ContinueWith(t => { if (!t.Result) _webFailures++; },
                TaskScheduler.FromCurrentSynchronizationContext());
            return _webReady;
        }

        private static async Task<bool> CreateWebAsync()
        {
            try
            {
                var host = MainForm.Current;
                if (host == null || host.IsDisposed) return false;

                _dir = Path.Combine(Path.GetTempPath(), "pismo_covers");
                Directory.CreateDirectory(_dir);

                // Visible=true обязателен: у невидимого элемента Chromium не
                // рисует кадры, и снять было бы нечего. Поэтому — видимый, но
                // за краем окна, как у транспорта звонка.
                _web = new WebView2
                {
                    Visible = true,
                    Size = new System.Drawing.Size(2, 2),
                    MinimumSize = new System.Drawing.Size(2, 2),
                    Location = new System.Drawing.Point(-3000, -3000),
                    Anchor = AnchorStyles.None,
                    TabStop = false,
                };
                host.Controls.Add(_web);
                _web.SendToBack();

                var env = await WebViewShared.GetAsync();
                try { if (!_web.IsHandleCreated) { _ = _web.Handle; } } catch { }
                Exception last = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try { await _web.EnsureCoreWebView2Async(env); last = null; break; }
                    catch (Exception ex)
                    {
                        last = ex;
                        await Task.Delay(300 * (attempt + 1));
                        try { if (!_web.IsHandleCreated) { _ = _web.Handle; } } catch { }
                    }
                }
                if (last != null || _web.CoreWebView2 == null) return false;

                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    Host, _dir, CoreWebView2HostResourceAccessKind.Allow);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _web.CoreWebView2.IsMuted = true;
                // Свой адрес для роликов с файлового сервиса (см. OnRemoteRequest).
                _web.CoreWebView2.AddWebResourceRequestedFilter(
                    "https://" + RemoteHost + "/*", CoreWebView2WebResourceContext.All);
                _web.CoreWebView2.WebResourceRequested += OnRemoteRequest;
                _web.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    string msg = null;
                    try { msg = e.TryGetWebMessageAsString(); } catch { }
                    var tcs = _frame;
                    if (tcs == null || msg == null) return;
                    if (msg.StartsWith("cover:"))
                    {
                        try { tcs.TrySetResult(Convert.FromBase64String(msg.Substring(6))); }
                        catch { tcs.TrySetResult(null); }
                    }
                    else if (msg == "fail") tcs.TrySetResult(null);
                };
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Страница-снимальщик: открыть ролик без звука, дождаться кадра
        /// чуть дальше начала (первый кадр часто чёрный), нарисовать его на
        /// canvas и отдать JPEG.
        /// </summary>
        private static string PageFor(string video, bool remote = false) => @"<!doctype html><html><head><meta charset='utf-8'></head>
<body style='margin:0;background:#000'>
<video id='v' muted playsinline preload='" + (remote ? "metadata" : "auto") + @"' src='" + System.Net.WebUtility.HtmlEncode(video) + @"'></video>
<script>
var v=document.getElementById('v'), sent=false;
function say(k){try{window.chrome.webview.postMessage(k);}catch(e){}}
function grab(){
  if(sent) return;
  if(!v.videoWidth||!v.videoHeight){ sent=true; say('fail'); return; }
  try{
    var k=Math.min(1,640/Math.max(v.videoWidth,v.videoHeight));
    var c=document.createElement('canvas');
    c.width=Math.max(1,Math.round(v.videoWidth*k));
    c.height=Math.max(1,Math.round(v.videoHeight*k));
    c.getContext('2d').drawImage(v,0,0,c.width,c.height);
    var u=c.toDataURL('image/jpeg',0.72);
    sent=true;
    if(u&&u.length>64) say('cover:'+u.substring(u.indexOf(',')+1)); else say('fail');
  }catch(e){ sent=true; say('fail'); }
  try{v.pause();}catch(e){}
}
// Кадр чуть дальше начала: первый у многих роликов чёрный. Перематываем,
// как только известна длина, — с сервиса так читается меньше всего.
var started=false;
function go(){
  if(started) return; started=true;
  var t=Math.min(0.6,(isFinite(v.duration)&&v.duration>0)?v.duration/4:0.6);
  try{ v.currentTime=t; }catch(e){ grab(); }
}
v.addEventListener('loadedmetadata',go);
v.addEventListener('loadeddata',go);
v.addEventListener('seeked',function(){ setTimeout(grab,60); });
v.addEventListener('error',function(){ if(!sent){ sent=true; say('fail'); } });
// Запасной путь: если перемотка не сработала, берём кадр после короткого
// проигрывания без звука.
setTimeout(function(){ if(!sent){ v.play().then(function(){ setTimeout(grab,300); }).catch(function(){}); } },4000);
setTimeout(function(){ if(!sent){ sent=true; say('fail'); } },18000);
</script></body></html>";
    }
}
