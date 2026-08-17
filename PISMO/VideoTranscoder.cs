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

            if (!NativeNvenc.FfmpegReady)
            {
                Say("Загружаем конвертер видео (~40 МБ, один раз)…");
                bool ok = await NativeNvenc.EnsureFfmpegAsync();
                if (!ok) return null;
            }

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
                string args =
                    $"-y -hide_banner -loglevel error -i \"{src}\" " +
                    "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
                    "-c:a aac -b:a 128k -movflags +faststart " +
                    $"\"{tmp}\"";

                int code = await RunAsync(NativeNvenc.FfmpegExe, args);
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

        private static Task<int> RunAsync(string exe, string args)
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
                p.Start();
                // Потоки нужно вычитывать, иначе процесс встанет на заполненном буфере.
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch { tcs.TrySetResult(-1); }
            return tcs.Task;
        }

        private static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).Substring(0, 32).ToLowerInvariant();
        }
    }
}
