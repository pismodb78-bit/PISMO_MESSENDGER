using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// PISMO 2.0 — нативный аппаратный энкодер демонстрации (в обход WebView2,
    /// который умеет только программный H264). Этап 1: находит/скачивает FFmpeg,
    /// проверяет доступность аппаратных энкодеров NVENC/Quick Sync и гоняет
    /// короткий бенчмарк «захват экрана (DXGI) + аппаратное кодирование», чтобы
    /// подтвердить, что железо реально выдаёт нужный fps.
    ///
    /// FFmpeg лежит в %LOCALAPPDATA%\PISMO\ffmpeg\ffmpeg.exe. Если его нет —
    /// скачивается статическая сборка (BtbN) один раз.
    /// </summary>
    public static class NativeNvenc
    {
        // СТАБИЛЬНАЯ сборка (gyan release): её NVENC совместим с обычными
        // драйверами. BtbN «master latest» требовал драйвер 610+ (свежайший SDK).
        private const string FfmpegZipUrl =
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        // Папка версионирована (r2), чтобы старая несовместимая сборка не
        // переиспользовалась — при обновлении URL качается заново.
        private static string BaseDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PISMO", "ffmpeg-r2");
        private static string FfmpegPath => Path.Combine(BaseDir, "ffmpeg.exe");

        /// <summary>Прогресс/лог наружу (для окна проверки).</summary>
        public static event Action<string> Log;
        private static void L(string s) { try { Log?.Invoke(s); } catch { } }

        /// <summary>Есть ли уже локальный ffmpeg.exe.</summary>
        public static bool FfmpegReady => File.Exists(FfmpegPath);

        /// <summary>Полный путь к ffmpeg.exe (может ещё не существовать).</summary>
        public static string FfmpegExe => FfmpegPath;

        /// <summary>Скачивает и распаковывает FFmpeg, если его ещё нет.</summary>
        public static async Task<bool> EnsureFfmpegAsync()
        {
            if (FfmpegReady) { L("FFmpeg уже установлен: " + FfmpegPath); return true; }
            try
            {
                Directory.CreateDirectory(BaseDir);
                string zip = Path.Combine(BaseDir, "ffmpeg.zip");
                L("Скачивание FFmpeg (~40 МБ)…");
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO");
                    var bytes = await http.GetByteArrayAsync(FfmpegZipUrl);
                    await File.WriteAllBytesAsync(zip, bytes);
                }
                L("Распаковка…");
                string tmp = Path.Combine(BaseDir, "extract");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                ZipFile.ExtractToDirectory(zip, tmp);
                // Ищем ffmpeg.exe в распакованном (обычно в bin/).
                string found = null;
                foreach (var f in Directory.GetFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories)) { found = f; break; }
                if (found == null) { L("ffmpeg.exe не найден в архиве."); return false; }
                File.Copy(found, FfmpegPath, true);
                try { File.Delete(zip); } catch { }
                try { Directory.Delete(tmp, true); } catch { }
                L("FFmpeg установлен: " + FfmpegPath);
                return true;
            }
            catch (Exception ex)
            {
                L("Ошибка загрузки FFmpeg: " + ex.Message);
                return false;
            }
        }

        /// <summary>Какие аппаратные энкодеры доступны (h264_nvenc / hevc_nvenc /
        /// h264_qsv …). Возвращает список найденных.</summary>
        public static async Task<string[]> DetectHwEncodersAsync()
        {
            if (!FfmpegReady) return Array.Empty<string>();
            string outp = await RunAsync("-hide_banner -encoders");
            var found = new System.Collections.Generic.List<string>();
            foreach (var enc in new[] { "h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv", "h264_amf", "hevc_amf" })
                if (outp.Contains(enc)) found.Add(enc);
            return found.ToArray();
        }

        /// <summary>Бенчмарк ПРОПУСКНОЙ СПОСОБНОСТИ энкодера: синтетический
        /// источник нужного размера кодируется как можно быстрее (без -re и без
        /// узкого места захвата). Возвращает достигнутый fps — это «сколько кадров
        /// в секунду энкодер способен выдать». >=60 значит realtime 1080p60 тянет.</summary>
        public static async Task<double> BenchmarkAsync(string encoder, int height, int targetFps, int seconds)
        {
            if (!FfmpegReady) return -1;
            int h = height > 0 ? height : 1080;
            int w = (int)Math.Round(h * 16.0 / 9.0 / 2) * 2;  // чётная ширина 16:9
            int frames = Math.Max(60, targetFps * seconds);

            // testsrc2 — синтетика (нагружает ТОЛЬКО энкодер). format=nv12 — то, что
            // ждёт NVENC/QSV. Кодируем frames кадров как можно быстрее → fps = потолок.
            string preset = encoder.Contains("nvenc") ? " -preset p1 -tune ll" : "";
            string args =
                $"-hide_banner -y -f lavfi -i testsrc2=size={w}x{h}:rate={targetFps} " +
                $"-frames:v {frames} -vf format=nv12 -c:v {encoder}{preset} -f null -";
            L($"Бенчмарк энкодера: {encoder}, {h}p, {frames} кадров…");
            string outp = await RunAsync(args);

            double fps = ParseFps(outp);
            if (fps < 0) L("Не удалось замерить (см. хвост лога ниже):\n" + Tail(outp, 12));
            else L($"Пропускная способность: ~{fps:0} fps ({(fps >= 60 ? "хватает на 60" : "ниже 60")}).");
            return fps;
        }

        /// <summary>Бенчмарк РЕАЛЬНОГО захвата экрана (DXGI ddagrab, аппаратно) +
        /// кодирование → пишет короткий mp4. Проверяет, что захват+энкод держат fps
        /// на живом экране. Возвращает (fps, путь к файлу) или (-1, лог).</summary>
        public static async Task<(double fps, string file)> BenchmarkCaptureAsync(string encoder, int height, int targetFps, int seconds)
        {
            if (!FfmpegReady) return (-1, "нет ffmpeg");
            string outFile = Path.Combine(BaseDir, $"capture_test_{height}p.mp4");
            string vf = height > 0
                ? $"-vf \"hwdownload,format=bgra,scale=-2:{height},format=nv12\""
                : "-vf \"hwdownload,format=bgra,format=nv12\"";
            string qArg = encoder.Contains("qsv") ? "-global_quality 20"
                        : encoder.Contains("nvenc") ? "-cq 20 -preset p4"
                        : "-qp 22";

            // ddagrab — DXGI Desktop Duplication (аппаратный захват основного монитора).
            string args =
                $"-hide_banner -y -f lavfi -i ddagrab=framerate={targetFps} -t {seconds} " +
                $"{vf} -c:v {encoder} {qArg} \"{outFile}\"";
            L($"Захват экрана (DXGI) + {encoder}, {(height > 0 ? height + "p" : "native")}@{targetFps}, {seconds}с…");
            string outp = await RunAsync(args);
            double fps = ParseFps(outp);

            // Фолбэк: ddagrab не поддержан → gdigrab (GDI, программный захват).
            if (fps < 0)
            {
                L("ddagrab не сработал, пробую gdigrab (программный захват)…");
                string vf2 = height > 0 ? $"-vf \"scale=-2:{height},format=nv12\"" : "-vf format=nv12";
                string args2 =
                    $"-hide_banner -y -f gdigrab -framerate {targetFps} -i desktop -t {seconds} " +
                    $"{vf2} -c:v {encoder} {qArg} \"{outFile}\"";
                outp = await RunAsync(args2);
                fps = ParseFps(outp);
            }

            if (fps < 0) { L("Захват не удался:\n" + Tail(outp, 12)); return (-1, outFile); }
            L($"Захват+кодирование: ~{fps:0} fps. Файл: {outFile}");
            return (fps, outFile);
        }

        private static double ParseFps(string outp)
        {
            double fps = -1;
            foreach (Match m in Regex.Matches(outp, @"fps=\s*([0-9]+(?:\.[0-9]+)?)"))
                if (double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0) fps = v;
            return fps;
        }

        private static string Tail(string s, int lines)
        {
            var arr = (s ?? "").Replace("\r", "").Split('\n');
            int from = Math.Max(0, arr.Length - lines);
            return string.Join("\n", arr[from..]);
        }

        private static async Task<string> RunAsync(string args)
        {
            var sb = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = new Process { StartInfo = psi };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                await p.WaitForExitAsync();
            }
            catch (Exception ex) { sb.AppendLine("run error: " + ex.Message); }
            return sb.ToString();
        }
    }
}
