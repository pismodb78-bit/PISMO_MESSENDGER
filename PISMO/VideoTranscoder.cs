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

            // Один конвертер на всё приложение: если открыть подряд несколько
            // «чёрных» видео, они не должны качать FFmpeg и грузить процессор
            // одновременно — встают в очередь.
            await _gate.WaitAsync();
            try
            {
                done = CachedPath(data);           // мог сконвертировать сосед по очереди
                if (done != null) return done;
                if (!await EnsureFfmpegAsync(Say)) return null;
                return await ConvertAsync(data, Say);
            }
            finally { _gate.Release(); }
        }

        private static readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

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

        private static readonly string[] FfmpegZipUrls =
        {
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://github.com/GyanD/codexffmpeg/releases/latest/download/ffmpeg-release-essentials.zip"
        };

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
                    Say("Загружаем конвертер видео (один раз)… 0%");

                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO");
                        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? 0;

                        using var net = await resp.Content.ReadAsStreamAsync();
                        using var file = File.Create(zip);
                        var buf = new byte[128 * 1024];
                        long got = 0;
                        int last = -1, n;
                        while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await file.WriteAsync(buf, 0, n);
                            got += n;
                            int pct = total > 0 ? (int)(got * 100 / total) : -1;
                            // Обновляем текст не чаще, чем раз в процент: иначе
                            // забьём очередь сообщений UI-потока.
                            if (pct != last)
                            {
                                last = pct;
                                Say(pct >= 0
                                    ? $"Загружаем конвертер видео (один раз)… {pct}%"
                                    : $"Загружаем конвертер видео (один раз)… {got / (1024 * 1024)} МБ");
                            }
                        }
                    }

                    Say("Распаковываем конвертер…");
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
                catch
                {
                    try { if (File.Exists(zip)) File.Delete(zip); } catch { }
                    // пробуем следующее зеркало
                }
            }

            Say("Не удалось загрузить конвертер видео.");
            return false;
        }

        private static void TryClean(string zip, string tmp)
        {
            try { File.Delete(zip); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
        }

        private static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).Substring(0, 32).ToLowerInvariant();
        }
    }
}
