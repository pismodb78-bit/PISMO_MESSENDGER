using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Перекодирование «непоказываемых» видео (в первую очередь HEVC/H.265) в
    /// обычный H.264, чтобы пользователю НЕ нужно было ставить кодек из Store.
    ///
    /// Декодер HEVC у FFmpeg свой, программный, и от системных кодеков не
    /// зависит — поэтому видео открывается даже на «голой» Windows. Сам FFmpeg
    /// уже умеет доставляться приложением (см. NativeNvenc.EnsureFfmpegAsync) —
    /// один раз качается ~40 МБ в %LOCALAPPDATA%\PISMO.
    ///
    /// Результат кэшируется по хэшу исходных байтов: одно и то же видео в чате
    /// конвертируется единожды, при повторных открытиях берётся готовый файл.
    /// </summary>
    internal static class VideoTranscoder
    {
        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "PISMO", "vidcache");

        /// <summary>Уже сконвертированный ранее файл, если он есть в кэше.</summary>
        public static string CachedPath(byte[] data)
        {
            try
            {
                string p = Path.Combine(CacheDir, Hash(data) + ".mp4");
                return File.Exists(p) && new FileInfo(p).Length > 0 ? p : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Готовит H.264-версию видео. Возвращает путь к готовому mp4 или null,
        /// если не получилось (нет сети для загрузки FFmpeg, конвертация упала).
        /// <paramref name="progress"/> вызывается с короткими статусами для плашки.
        /// </summary>
        public static async Task<string> ToH264Async(byte[] data, Action<string> progress)
        {
            if (data == null || data.Length == 0) return null;

            string done = CachedPath(data);
            if (done != null) return done;

            void Say(string s) { try { progress?.Invoke(s); } catch { } }

            // Загрузка конвертера — ОБЩАЯ на всё приложение и идёт вне семафора:
            // её мог уже начать фоновый Prefetch. Подписываемся на её статус,
            // иначе плашка молча ждала бы чужую загрузку («Готовим видео…» и
            // ничего дальше).
            if (!NativeNvenc.FfmpegReady)
            {
                Say(_status ?? "Загружаем конвертер видео (один раз)…");
                Action<string> relay = s => Say(s);
                Status += relay;
                try { if (!await EnsureFfmpegSharedAsync()) { Say("Не удалось загрузить конвертер видео."); return null; } }
                finally { Status -= relay; }
            }

            // А вот сама перекодировка — по одной за раз: несколько видео разом
            // просто задушили бы процессор.
            await _gate.WaitAsync();
            try
            {
                done = CachedPath(data);           // мог сконвертировать сосед по очереди
                if (done != null) return done;
                return await ConvertAsync(data, Say);
            }
            finally { _gate.Release(); }
        }

        private static readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

        /// <summary>Последний статус загрузки конвертера и подписка на него —
        /// чтобы плашка показывала прогресс общей, в том числе фоновой, загрузки.</summary>
        private static string _status;
        private static event Action<string> Status;
        private static void Publish(string s)
        {
            _status = s;
            try { Status?.Invoke(s); } catch { }
        }

        private static readonly object _ffmpegLock = new();
        private static Task<bool> _ffmpegTask;

        /// <summary>Одна общая задача загрузки: сколько бы плееров ни попросило
        /// конвертер, качается он ровно один раз.</summary>
        private static Task<bool> EnsureFfmpegSharedAsync()
        {
            if (NativeNvenc.FfmpegReady) return Task.FromResult(true);
            lock (_ffmpegLock)
            {
                if (_ffmpegTask == null || (_ffmpegTask.IsCompleted && _ffmpegTask.Result == false))
                    _ffmpegTask = Task.Run(() => EnsureFfmpegAsync(Publish));
                return _ffmpegTask;
            }
        }

        private static async Task<string> ConvertAsync(byte[] data, Action<string> Say)
        {
            string src = null, dst = null;
            try
            {
                Directory.CreateDirectory(CacheDir);
                string hash = Hash(data);
                src = Path.Combine(CacheDir, hash + ".src");
                dst = Path.Combine(CacheDir, hash + ".mp4");
                string tmp = dst + ".part";

                if (!File.Exists(src)) await File.WriteAllBytesAsync(src, data);

                Say("Перекодируем видео…");
                // veryfast/crf 23 — компромисс: заметно быстрее «разумного»
                // качества почти без потери картинки. faststart переносит moov
                // в начало, иначе плеер ждёт весь файл перед стартом.
                // -progress pipe:1 даёт машиночитаемый поток состояния — по нему
                // показываем, сколько секунд видео уже обработано, иначе плашка
                // просто висит и выглядит как зависание.
                string args =
                    $"-y -hide_banner -loglevel error -progress pipe:1 -nostats -i \"{src}\" " +
                    "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 128k -movflags +faststart " +
                    $"\"{tmp}\"";

                int code = await RunAsync(NativeNvenc.FfmpegExe, args, line =>
                {
                    // out_time_us=1234567 / out_time_ms=… — сколько видео пройдено.
                    if (line == null) return;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) return;
                    string key = line.Substring(0, eq);
                    if (key != "out_time_us" && key != "out_time_ms") return;
                    if (!long.TryParse(line.Substring(eq + 1), out long v) || v <= 0) return;
                    // out_time_ms у ffmpeg на самом деле в микросекундах — обе
                    // ветки делим одинаково.
                    Say($"Перекодируем видео… {v / 1_000_000} с");
                });
                if (code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return null;
                }

                try { if (File.Exists(dst)) File.Delete(dst); } catch { }
                File.Move(tmp, dst);
                try { File.Delete(src); } catch { }   // исходник больше не нужен
                return dst;
            }
            catch
            {
                return null;
            }
        }

        private static Task<int> RunAsync(string exe, string args, Action<string> onLine = null)
        {
            var tcs = new TaskCompletionSource<int>();
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    },
                    EnableRaisingEvents = true
                };
                p.Exited += (s, e) => { try { tcs.TrySetResult(p.ExitCode); } catch { tcs.TrySetResult(-1); } finally { try { p.Dispose(); } catch { } } };
                // Потоки нужно вычитывать, иначе процесс встанет на заполненном буфере.
                p.OutputDataReceived += (s, e) => { if (e.Data != null) { try { onLine?.Invoke(e.Data); } catch { } } };
                p.ErrorDataReceived += (s, e) => { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch { tcs.TrySetResult(-1); }
            return tcs.Task;
        }

        // ── Доставка FFmpeg ──────────────────────────────────────────────────
        // Своя загрузка вместо NativeNvenc.EnsureFfmpegAsync: там нет прогресса
        // (плашка молча висела), нет запасного зеркала и таймаут в 10 минут.
        // Кладём в ту же папку, что и NVENC-путь, — конвертер общий.

        // Порядок важен: gyan.dev раздаёт медленно (заметно режет одно
        // соединение), GitHub-релизы того же самого архива идут через CDN и
        // намного быстрее — поэтому они первыми.
        private static readonly string[] FfmpegZipUrls =
        {
            "https://github.com/GyanD/codexffmpeg/releases/latest/download/ffmpeg-release-essentials.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
        };

        /// <summary>
        /// Заранее подтягивает конвертер в фоне (без плашки), чтобы к моменту
        /// клика по видео он уже лежал на диске. Вызывается, когда в чате
        /// появилось видео, которое системе показать нечем.
        /// </summary>
        public static void Prefetch()
        {
            if (NativeNvenc.FfmpegReady) return;
            try { _ = EnsureFfmpegSharedAsync(); } catch { }
        }

        private static async Task<bool> EnsureFfmpegAsync(Action<string> Say)
        {
            if (NativeNvenc.FfmpegReady) return true;

            string exe = NativeNvenc.FfmpegExe;
            string dir = Path.GetDirectoryName(exe);
            string zip = Path.Combine(dir, "ffmpeg.zip");

            foreach (var url in FfmpegZipUrls)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    Log("Загрузка конвертера: " + url);
                    if (!await DownloadAsync(url, zip, Say)) { Log("Зеркало не отдало файл, пробуем следующее"); continue; }
                    Log("Скачано, байт: " + new FileInfo(zip).Length);

                    Say?.Invoke("Распаковываем конвертер…");
                    string tmp = Path.Combine(dir, "extract");
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);

                    string found = null;
                    foreach (var f in Directory.GetFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories)) { found = f; break; }
                    if (found == null) { TryClean(zip, tmp); continue; }

                    File.Copy(found, exe, true);
                    TryClean(zip, tmp);
                    return true;
                }
                catch (Exception ex)
                {
                    Log("Ошибка зеркала: " + ex.Message);
                    try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                    // пробуем следующее зеркало
                }
            }

            Log("Все зеркала не отработали.");
            Say?.Invoke("Не удалось загрузить конвертер видео.");
            return false;
        }

        /// <summary>
        /// Качает файл, по возможности в НЕСКОЛЬКО потоков: раздачи обычно режут
        /// скорость на одно соединение, и 40 МБ в один поток тянутся минутами.
        /// Если сервер не поддерживает Range — обычная последовательная загрузка.
        /// </summary>
        private static async Task<bool> DownloadAsync(string url, string dest, Action<string> Say)
        {
            const int Parts = 4;
            long got = 0, total = 0;
            var sw = Stopwatch.StartNew();
            int lastPct = -1;

            void Tick(int add)
            {
                long g = System.Threading.Interlocked.Add(ref got, add);
                int pct = total > 0 ? (int)(g * 100 / total) : -1;
                if (pct == lastPct) return;          // не чаще раза на процент
                lastPct = pct;
                double mbps = sw.Elapsed.TotalSeconds > 0.5 ? g / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds : 0;
                string speed = mbps > 0.05 ? $", {mbps:0.0} МБ/с" : "";
                Say?.Invoke(pct >= 0
                    ? $"Загружаем конвертер видео (один раз)… {pct}%{speed}"
                    : $"Загружаем конвертер видео (один раз)… {g / (1024 * 1024)} МБ{speed}");
            }

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO");

            bool ranges = false;
            try
            {
                using var head = await http.SendAsync(
                    new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url),
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                if (head.IsSuccessStatusCode)
                {
                    total = head.Content.Headers.ContentLength ?? 0;
                    ranges = head.Headers.AcceptRanges.Contains("bytes");
                }
                Log($"HEAD: {(int)head.StatusCode}, размер {total}, Range {ranges}");
            }
            catch (Exception ex) { Log("HEAD не прошёл: " + ex.Message); }

            Say?.Invoke("Загружаем конвертер видео (один раз)… 0%");

            if (ranges && total > 8L * 1024 * 1024)
            {
                var parts = new string[Parts];
                try
                {
                    long chunk = total / Parts;
                    var jobs = new Task[Parts];
                    for (int i = 0; i < Parts; i++)
                    {
                        long from = i * chunk;
                        long to = (i == Parts - 1) ? total - 1 : from + chunk - 1;
                        parts[i] = dest + ".p" + i;
                        string path = parts[i];
                        jobs[i] = Task.Run(async () =>
                        {
                            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                            using var r = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                            r.EnsureSuccessStatusCode();
                            using var net = await r.Content.ReadAsStreamAsync();
                            using var f = File.Create(path);
                            var buf = new byte[128 * 1024];
                            int n;
                            while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0)
                            {
                                await f.WriteAsync(buf, 0, n);
                                Tick(n);
                            }
                        });
                    }
                    await Task.WhenAll(jobs);

                    // Склеиваем куски по порядку.
                    using (var outf = File.Create(dest))
                        foreach (var p in parts)
                            using (var inf = File.OpenRead(p))
                                await inf.CopyToAsync(outf);

                    foreach (var p in parts) { try { File.Delete(p); } catch { } }
                    return new FileInfo(dest).Length == total;
                }
                catch (Exception ex)
                {
                    Log("Многопоточная загрузка не удалась: " + ex.Message);
                    foreach (var p in parts) { try { if (p != null) File.Delete(p); } catch { } }
                    // не вышло — падаем в обычную загрузку ниже
                    got = 0; lastPct = -1; sw.Restart();
                }
            }

            try
            {
                using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                if (total == 0) total = resp.Content.Headers.ContentLength ?? 0;

                using var net = await resp.Content.ReadAsStreamAsync();
                using var file = File.Create(dest);
                var buf = new byte[128 * 1024];
                int n;
                while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await file.WriteAsync(buf, 0, n);
                    Tick(n);
                }
                return true;
            }
            catch (Exception ex) { Log("Загрузка не удалась: " + ex.Message); return false; }
        }

        private static void TryClean(string zip, string tmp)
        {
            try { File.Delete(zip); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
        }

        /// <summary>Диагностика в %LOCALAPPDATA%\PISMO\video_convert.log: без неё
        /// любая сетевая ошибка выглядела просто как «ничего не происходит».</summary>
        private static void Log(string s)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PISMO");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "video_convert.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            }
            catch { }
        }

        private static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).Substring(0, 32).ToLowerInvariant();
        }
    }
}
