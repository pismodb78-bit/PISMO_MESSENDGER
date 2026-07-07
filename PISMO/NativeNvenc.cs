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
        private const string FfmpegZipUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

        private static string BaseDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PISMO", "ffmpeg");
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

        /// <summary>Бенчмарк: захват экрана (ddagrab — DXGI, аппаратный) +
        /// кодирование выбранным энкодером на targetFps в течение seconds секунд.
        /// Возвращает достигнутый fps (по строке 'frame= … fps=…' от FFmpeg) или -1.</summary>
        public static async Task<double> BenchmarkAsync(string encoder, int height, int targetFps, int seconds)
        {
            if (!FfmpegReady) return -1;
            // ddagrab захватывает основной монитор через DXGI Desktop Duplication
            // (аппаратно), scale масштабирует до нужной высоты, энкодер кодирует,
            // вывод в null (нам важен только fps кодирования).
            string vf = height > 0 ? $"-vf \"scale=-2:{height}\"" : "";
            string args =
                $"-hide_banner -y -f lavfi -i ddagrab=framerate={targetFps} -t {seconds} " +
                $"{vf} -c:v {encoder} -preset p1 -tune ll -f null -";
            L($"Бенчмарк: {encoder}, {(height > 0 ? height + "p" : "native")}@{targetFps}, {seconds}с…");
            string outp = await RunAsync(args);

            // Из stderr FFmpeg берём последний 'fps='.
            double fps = -1;
            foreach (Match m in Regex.Matches(outp, @"fps=\s*([0-9]+(?:\.[0-9]+)?)"))
                if (double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v)) fps = v;
            // Если ddagrab недоступен (старый ffmpeg/нет DXGI) — пробуем gdigrab.
            if (fps < 0 && args.Contains("ddagrab"))
            {
                L("ddagrab недоступен, пробую gdigrab…");
                string args2 =
                    $"-hide_banner -y -f gdigrab -framerate {targetFps} -i desktop -t {seconds} " +
                    $"{vf} -c:v {encoder} -preset p1 -tune ll -f null -";
                string o2 = await RunAsync(args2);
                foreach (Match m in Regex.Matches(o2, @"fps=\s*([0-9]+(?:\.[0-9]+)?)"))
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v)) fps = v;
            }
            L(fps >= 0 ? $"Достигнуто ~{fps:0} fps кодирования." : "Не удалось замерить fps (см. лог).");
            return fps;
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
