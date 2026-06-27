using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Автообновление через GitHub Releases (как у Discord — проверка при запуске).
    ///
    /// При старте приложение запрашивает последний релиз репозитория, сравнивает
    /// версию с текущей и, если есть новее, предлагает обновиться: качает .zip
    /// ассет, и через временный .bat (который ждёт закрытия PISMO.exe) распаковывает
    /// его поверх папки программы и перезапускает её.
    ///
    /// Чтобы выпустить обновление: собрать релиз (dotnet publish), сложить файлы
    /// в .zip (файлы в КОРНЕ архива, не во вложенной папке), создать на GitHub
    /// новый Release с тегом вида v1.0.1 и приложить этот .zip.
    /// </summary>
    public static class Updater
    {
        // Репозиторий с релизами.
        private const string Owner = "pismodb78-bit";
        private const string Repo = "pismo_messendger";

        private static readonly string ApiUrl =
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        /// <summary>Проверка обновлений при запуске (блокирующая, с коротким
        /// таймаутом). Любая ошибка/отсутствие сети — молча продолжаем работу.</summary>
        public static void CheckOnStartup()
        {
            try { CheckAsync().GetAwaiter().GetResult(); }
            catch { /* нет сети / GitHub недоступен — не мешаем запуску */ }
        }

        private static async Task CheckAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json;
            try { json = await http.GetStringAsync(ApiUrl); }
            catch { return; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (!TryParseVersion(tag, out var remote)) return;
            if (remote <= current) return; // уже актуальная версия

            // Ищем .zip-ассет релиза.
            string zipUrl = null, zipName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipName = name;
                        zipUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(zipUrl)) return;

            string notes = root.TryGetProperty("body", out var b) ? b.GetString() : "";
            string msg = $"Доступна новая версия PISMO {tag} (у вас {current.Major}.{current.Minor}.{current.Build}).\n\n" +
                         (string.IsNullOrWhiteSpace(notes) ? "" : (notes.Length > 400 ? notes.Substring(0, 400) + "…" : notes) + "\n\n") +
                         "Обновить сейчас? Программа перезапустится.";

            var res = MessageBox.Show(msg, "PISMO — обновление",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (res != DialogResult.Yes) return;

            await DownloadAndApplyAsync(http, zipUrl, zipName);
        }

        private static async Task DownloadAndApplyAsync(HttpClient http, string zipUrl, string zipName)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), "pismo_update_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                var bytes = await http.GetByteArrayAsync(zipUrl);
                File.WriteAllBytes(tempZip, bytes);
            }
            catch
            {
                MessageBox.Show("Не удалось скачать обновление. Попробуйте позже.", "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory; // оканчивается на '\'
            string exePath = Path.Combine(appDir, "PISMO.exe");
            string batPath = Path.Combine(Path.GetTempPath(), "pismo_update.bat");

            // .bat ждёт закрытия PISMO.exe, распаковывает .zip поверх папки и перезапускает.
            string bat =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                ":wait\r\n" +
                "tasklist /fi \"imagename eq PISMO.exe\" | find /i \"PISMO.exe\" >nul\r\n" +
                "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
                $"powershell -NoProfile -Command \"Expand-Archive -Force -LiteralPath '{tempZip}' -DestinationPath '{appDir.TrimEnd('\\')}'\"\r\n" +
                $"start \"\" \"{exePath}\"\r\n" +
                $"del \"{tempZip}\"\r\n" +
                "del \"%~f0\"\r\n";

            File.WriteAllText(batPath, bat, System.Text.Encoding.UTF8);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
            Environment.Exit(0); // закрываем приложение, чтобы .bat смог заменить файлы
        }

        /// <summary>Парсит тег релиза (v1.2.3 / 1.2.3) в Version.</summary>
        private static bool TryParseVersion(string tag, out Version version)
        {
            version = null;
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            // оставляем только цифры и точки
            var clean = new string(Array.FindAll(s.ToCharArray(), c => char.IsDigit(c) || c == '.'));
            if (string.IsNullOrWhiteSpace(clean)) return false;
            // Version требует минимум major.minor
            if (!clean.Contains('.')) clean += ".0";
            return Version.TryParse(clean, out version);
        }
    }
}
