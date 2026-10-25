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
        private const long AutoDownloadLimit = 40L * 1024 * 1024;

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
            /// <summary>Заменить имеющуюся (чёрную) обложку, а не только пустую.</summary>
            public bool Replace;
        }

        private static readonly List<Job> _queue = new();
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
                                   string cacheKind, bool cached, long size, Func<byte[]> loader,
                                   bool replace = false)
        {
            if (msgId <= 0 || loader == null || string.IsNullOrEmpty(table)) return;
            // Очередь и невидимый движок живут на потоке интерфейса.
            var ui = MainForm.Current;
            if (ui != null && !ui.IsDisposed && ui.InvokeRequired)
            {
                try { ui.BeginInvoke(new Action(() => Request(player, table, msgId, fileName, cacheKind, cached, size, loader, replace))); }
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

            _queue.Add(new Job
            {
                Key = key, Table = table, MsgId = msgId, FileName = fileName ?? "video.mp4",
                CacheKind = cacheKind ?? "img", Loader = loader,
                Cached = cached, Size = size, Replace = replace,
            });
            _ = PumpAsync();
        }

        /// <summary>
        /// У ролика уже есть обложка, но она может оказаться ЧЁРНОЙ (снят был
        /// самый первый кадр). Проверяем в фоне и, если чёрная, переснимаем
        /// светлый кадр и заменяем.
        /// </summary>
        public static void RecheckDark(InlineVideoPlayer player, byte[] poster, string table, int msgId,
                                       string fileName, string cacheKind, bool cached, long size,
                                       Func<byte[]> loader)
        {
            if (poster == null || poster.Length == 0 || msgId <= 0) return;
            if (_darkChecked.Contains(table + ":" + msgId)) return;
            _darkChecked.Add(table + ":" + msgId);
            Task.Run(() =>
            {
                if (!VideoCover.IsDark(poster)) return;
                Request(player, table, msgId, fileName, cacheKind, cached, size, loader, replace: true);
            });
        }

        /// <summary>
        /// То же для ячейки альбома: обложку берём из кеша в фоне (её мог ещё не
        /// привезти никто — тогда и проверять нечего).
        /// </summary>
        public static void RecheckDarkCached(string table, int msgId, string fileName, string cacheKind,
                                             bool cached, long size, Func<byte[]> loader)
        {
            if (msgId <= 0) return;
            if (_darkChecked.Contains(table + ":" + msgId)) return;
            _darkChecked.Add(table + ":" + msgId);
            Task.Run(() =>
            {
                byte[] poster = null;
                try { poster = MediaCache.Get(msgId, cacheKind, null); } catch { }
                if (poster == null || poster.Length == 0 || !VideoCover.IsDark(poster)) return;
                Request(null, table, msgId, fileName, cacheKind, cached, size, loader, replace: true);
            });
        }

        /// <summary>Чьи обложки уже проверены на черноту в этом запуске.</summary>
        private static readonly HashSet<string> _darkChecked = new();

        private static async Task PumpAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                while (_queue.Count > 0)
                {
                    // Сначала — запрошенное ПОСЛЕДНИМ: это то, что сейчас на
                    // экране. Очередью «по порядку» человек ждал, пока
                    // разберутся ролики, давно уехавшие из виду.
                    var job = _queue[_queue.Count - 1];
                    _queue.RemoveAt(_queue.Count - 1);
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
            if (job.Replace) VideoCover.Replace(job.Table, job.MsgId, jpeg, job.CacheKind);
            else VideoCover.Share(job.Table, job.MsgId, jpeg, job.CacheKind);
            try { CoverReady?.Invoke(job.MsgId); } catch { }

            // И сразу в плееры, которые её ждали.
            if (_waiters.TryGetValue(job.Key, out var list))
            {
                foreach (var w in list)
                    if (w.TryGetTarget(out var p) && !p.IsDisposed && (p.Poster == null || job.Replace))
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
                // Лежит в базе целиком: докачиваем только небольшие. Размер
                // неизвестен (старые строки без file_size) — спрашиваем базу
                // один раз: иначе такие ролики не получали обложку никогда,
                // только после запуска.
                if (job.Size <= 0)
                {
                    job.Size = await Task.Run(() =>
                    {
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            using var cmd = new MySqlConnector.MySqlCommand(
                                $"SELECT OCTET_LENGTH(file_data) FROM {job.Table} WHERE id=@id", conn)
                            { CommandTimeout = 60 };
                            cmd.Parameters.AddWithValue("@id", job.MsgId);
                            var o = cmd.ExecuteScalar();
                            return o == null || o == DBNull.Value ? 0L : Convert.ToInt64(o);
                        }
                        catch { return 0L; }
                    });
                }
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
                    // Не точкой 2×2: ролик должен лечь в окно страницы целиком,
                    // иначе движок считает его невидимым и кадры не готовит.
                    Size = new System.Drawing.Size(160, 90),
                    MinimumSize = new System.Drawing.Size(160, 90),
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
<body style='margin:0;background:#000;overflow:hidden'>
<video id='v' muted playsinline preload='" + (remote ? "metadata" : "auto") + @"' src='" + System.Net.WebUtility.HtmlEncode(video) + @"'
  style='position:fixed;left:0;top:0;width:100%;height:100%;object-fit:contain'></video>
<script>
var v=document.getElementById('v'), sent=false;
function say(k){try{window.chrome.webview.postMessage(k);}catch(e){}}
// Яркость кадра: среднее по уменьшенной копии. Первый кадр у многих роликов
// чёрный (затемнение в начале, заставка), и обложка выходила чёрным квадратом.
function lum(){
  try{
    var c=document.createElement('canvas'); c.width=24; c.height=24;
    var x=c.getContext('2d'); x.drawImage(v,0,0,24,24);
    var d=x.getImageData(0,0,24,24).data, s=0;
    for(var i=0;i<d.length;i+=4) s+=0.299*d[i]+0.587*d[i+1]+0.114*d[i+2];
    return s/(d.length/4);
  }catch(e){ return 255; }
}
function shot(){
  var k=Math.min(1,640/Math.max(v.videoWidth,v.videoHeight));
  var c=document.createElement('canvas');
  c.width=Math.max(1,Math.round(v.videoWidth*k));
  c.height=Math.max(1,Math.round(v.videoHeight*k));
  c.getContext('2d').drawImage(v,0,0,c.width,c.height);
  return c.toDataURL('image/jpeg',0.72);
}
function finish(u){
  if(sent) return; sent=true;
  if(u&&u.length>64) say('cover:'+u.substring(u.indexOf(',')+1)); else say('fail');
  try{v.pause();}catch(e){}
}
// Несколько моментов ролика по очереди: берём первый НЕ тёмный кадр, а если
// светлых нет вовсе — самый светлый из увиденных.
var times=[], ti=0, best=null, bestL=-1;
function plan(){
  var d=(isFinite(v.duration)&&v.duration>0)?v.duration:0;
  var cand=[0.6,1.5,3,d*0.1,d*0.25,d*0.5];
  for(var i=0;i<cand.length;i++){
    var t=cand[i]; if(d>0) t=Math.min(t,Math.max(0,d-0.1));
    if(t<0) continue;
    var dup=false; for(var j=0;j<times.length;j++) if(Math.abs(times[j]-t)<0.3) dup=true;
    if(!dup) times.push(t);
  }
  times.sort(function(a,b){return a-b;});
}
function next(){
  if(sent) return;
  if(ti>=times.length){ finish(best); return; }
  try{ v.currentTime=times[ti++]; }catch(e){ finish(best); }
}
function look(){
  if(sent) return;
  if(!v.videoWidth||!v.videoHeight){ finish(null); return; }
  var l=lum();
  try{
    if(l>=22){ finish(shot()); return; }
    if(l>bestL){ bestL=l; best=shot(); }
  }catch(e){ finish(best); return; }
  next();
}
var started=false;
function go(){ if(started) return; started=true; plan(); next(); }
v.addEventListener('loadedmetadata',go);
v.addEventListener('loadeddata',go);
v.addEventListener('seeked',function(){ setTimeout(look,60); });
v.addEventListener('error',function(){ finish(best); });
// Запасной путь: перемотка не сработала — берём кадр после короткого
// проигрывания без звука.
setTimeout(function(){ if(!sent&&ti<=1){ v.play().then(function(){ setTimeout(look,600); }).catch(function(){}); } },4000);
setTimeout(function(){ finish(best); },18000);
</script></body></html>";
    }
}
