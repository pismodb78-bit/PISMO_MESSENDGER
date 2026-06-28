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
                // Отдельный клиент с большим таймаутом: проверочный http имеет 6 сек,
                // чего НЕ хватает на скачивание полного архива (рантайм WebView2 и т.д.).
                using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                dl.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO-Updater");
                var bytes = await dl.GetByteArrayAsync(zipUrl);
                File.WriteAllBytes(tempZip, bytes);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось скачать обновление. Попробуйте позже.\n\n" + ex.Message, "PISMO",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string ps1Path = Path.Combine(Path.GetTempPath(), "pismo_update.ps1");

            // PowerShell-апдейтер: ждёт закрытия PISMO, распаковывает архив во
            // временную папку, НАХОДИТ PISMO.exe внутри (устойчиво к вложенным
            // папкам в архиве), копирует эту папку поверх приложения и перезапускает.
            string ps =
                "$ErrorActionPreference='SilentlyContinue'\r\n" +
                "Start-Sleep -Seconds 1\r\n" +
                "for($i=0;$i -lt 60;$i++){ if(-not (Get-Process -Name 'PISMO' -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 700 }\r\n" +
                $"$zip='{tempZip.Replace("'", "''")}'\r\n" +
                $"$app='{appDir.Replace("'", "''")}'\r\n" +
                "$tmp=Join-Path $env:TEMP ('pismo_ext_'+[guid]::NewGuid().ToString('N'))\r\n" +
                "Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force\r\n" +
                "$exe=Get-ChildItem -Path $tmp -Recurse -Filter 'PISMO.exe' | Select-Object -First 1\r\n" +
                "if($exe){ $src=$exe.Directory.FullName; Copy-Item -Path (Join-Path $src '*') -Destination $app -Recurse -Force }\r\n" +
                "Start-Process -FilePath (Join-Path $app 'PISMO.exe')\r\n" +
                "Remove-Item $zip -Force; Remove-Item $tmp -Recurse -Force\r\n";

            File.WriteAllText(ps1Path, ps, new System.Text.UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
            Environment.Exit(0); // закрываем приложение, чтобы апдейтер смог заменить файлы
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
