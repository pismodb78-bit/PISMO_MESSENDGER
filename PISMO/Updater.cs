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
            try { CheckAsync(null).GetAwaiter().GetResult(); }
            catch { /* нет сети / GitHub недоступен — не мешаем запуску */ }
        }

        /// <summary>Асинхронная проверка обновлений (вызывается из заставки, не
        /// блокирует её отрисовку). owner — окно-владелец для диалога.
        /// Возвращает true, если запущено обновление (приложение закроется).</summary>
        public static async Task<bool> CheckInteractiveAsync(IWin32Window owner)
        {
            try { return await CheckAsync(owner); }
            catch { return false; }
        }

        /// <summary>HttpClient с ЯВНЫМИ TLS 1.2/1.3. У части пользователей системные
        /// умолчания SSL сломаны (старые шифры/политики) — «The SSL connection
        /// could not be established»; явный список протоколов чинит рукопожатие.
        /// Проверка сертификатов остаётся строгой (обновления подменять нельзя).</summary>
        private static HttpClient MakeHttp(TimeSpan timeout)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true,   // GitHub отдаёт ассет через redirect на objects.githubusercontent.com
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13
                }
            };
            var http = new HttpClient(handler) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO-Updater");
            return http;
        }

        private static async Task<bool> CheckAsync(IWin32Window owner)
        {
            using var http = MakeHttp(TimeSpan.FromSeconds(6));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string json;
            try { json = await http.GetStringAsync(ApiUrl); }
            catch { return false; }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return false;

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (!TryParseVersion(tag, out var remote)) return false;
            if (remote <= current) return false; // уже актуальная версия

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
            if (string.IsNullOrWhiteSpace(zipUrl)) return false;

            string notes = root.TryGetProperty("body", out var b) ? b.GetString() : "";
            string msg = $"Доступна новая версия PISMO {tag} (у вас {current.Major}.{current.Minor}.{current.Build}).\n\n" +
                         (string.IsNullOrWhiteSpace(notes) ? "" : (notes.Length > 400 ? notes.Substring(0, 400) + "…" : notes) + "\n\n") +
                         "Обновить сейчас? Программа перезапустится.";

            var res = MessageBox.Show(owner, msg, "PISMO — обновление",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (res != DialogResult.Yes) return false;

            return await DownloadAndApplyAsync(http, zipUrl, zipName);
        }

        private static async Task<bool> DownloadAndApplyAsync(HttpClient http, string zipUrl, string zipName)
        {
            string tempZip = Path.Combine(Path.GetTempPath(), "pismo_update_" + Guid.NewGuid().ToString("N") + ".zip");
            Exception lastErr = null;
            bool downloaded = false;
            // До 3 попыток с паузой: разовые обрывы TLS/сети — обычное дело.
            for (int attempt = 1; attempt <= 3 && !downloaded; attempt++)
            {
                try
                {
                    // Отдельный клиент с большим таймаутом: проверочный имеет 6 сек,
                    // чего НЕ хватает на скачивание полного архива.
                    using var dl = MakeHttp(TimeSpan.FromMinutes(10));
                    var bytes = await dl.GetByteArrayAsync(zipUrl);
                    if (bytes == null || bytes.Length < 1024)
                        throw new IOException("архив пустой/битый (" + (bytes?.Length ?? 0) + " байт)");
                    File.WriteAllBytes(tempZip, bytes);
                    downloaded = true;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                    if (attempt < 3) await Task.Delay(1500 * attempt);
                }
            }
            if (!downloaded)
            {
                // Показываем и внутреннюю причину — по ней видно, ЧТО именно с TLS
                // (часы системы, антивирус-перехват, фильтрация сети).
                string detail = lastErr?.Message ?? "";
                if (lastErr?.InnerException != null) detail += "\n→ " + lastErr.InnerException.Message;
                MessageBox.Show(
                    "Не удалось скачать обновление. Попробуйте позже.\n\n" + detail +
                    "\n\nЧастые причины: сбитые дата/время на компьютере, антивирус или " +
                    "сеть, режущая доступ к github.com. Архив также можно скачать вручную " +
                    "со страницы релизов и распаковать поверх папки программы.",
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string ps1Path = Path.Combine(Path.GetTempPath(), "pismo_update.ps1");
            string logPath = Path.Combine(Path.GetTempPath(), "pismo_update.log");

            // Есть ли право писать в папку приложения (Program Files — нет)?
            // Если нет — апдейтер запускается с повышением прав (UAC), иначе
            // копирование молча проваливалось и перезапускалась СТАРАЯ версия,
            // которая снова предлагала то же обновление.
            bool canWrite = true;
            try
            {
                string probe = Path.Combine(appDir, ".pismo_write_test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch { canWrite = false; }

            int selfPid = Process.GetCurrentProcess().Id;

            // PowerShell-апдейтер. ГЛАВНОЕ отличие: вся работа в try, а перезапуск
            // PISMO — в finally, поэтому приложение ВОЗВРАЩАЕТСЯ пользователю ВСЕГДА,
            // даже если распаковка/копирование упали с терминирующей ошибкой (иначе
            // получалось «нажал Да → процесса нет»). Логи — простым Add-Content
            // (Start-Transcript мог сам кинуть терминирующую ошибку и убить скрипт).
            string ps =
                "$ErrorActionPreference='Continue'\r\n" +
                $"$self={selfPid}\r\n" +
                $"$zip='{tempZip.Replace("'", "''")}'\r\n" +
                $"$app='{appDir.Replace("'", "''")}'\r\n" +
                $"$log='{logPath.Replace("'", "''")}'\r\n" +
                "function Log($m){ try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('HH:mm:ss')+' '+$m) } catch {} }\r\n" +
                // Закрываем и PISMO, и дочерние процессы WebView2 (msedgewebview2) —
                // именно они держали файлы и не давали их перезаписать (обновление
                // «зависало»/не применялось).
                "function Kill-Pismo { \r\n" +
                "  try { Stop-Process -Id $self -Force -ErrorAction SilentlyContinue } catch {}\r\n" +
                "  try { Get-Process -Name 'PISMO' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}\r\n" +
                "  try { Get-Process -Name 'msedgewebview2' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}\r\n" +
                "}\r\n" +
                "$ok=$false; $tmp=$null\r\n" +
                "Log 'updater start'\r\n" +
                "try {\r\n" +
                // Даём приложению самому закрыться (Environment.Exit), затем добиваем.
                "  for($i=0;$i -lt 20;$i++){ if(-not (Get-Process -Name 'PISMO' -ErrorAction SilentlyContinue)){break}; Start-Sleep -Milliseconds 500 }\r\n" +
                "  Kill-Pismo\r\n" +
                "  Start-Sleep -Seconds 2\r\n" +   // даём ОС отпустить дескрипторы (WebView2/FFI)
                "  $tmp=Join-Path $env:TEMP ('pismo_ext_'+[guid]::NewGuid().ToString('N'))\r\n" +
                "  try { Unblock-File -LiteralPath $zip -ErrorAction SilentlyContinue } catch {}\r\n" +
                "  Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force\r\n" +
                "  $exe=Get-ChildItem -Path $tmp -Recurse -Filter 'PISMO.exe' | Select-Object -First 1\r\n" +
                "  if($exe){\r\n" +
                "    $src=$exe.Directory.FullName\r\n" +
                // robocopy НАДЁЖНЕЕ Copy-Item: не падает целиком из-за одного занятого
                // файла, сам повторяет (/R:5 /W:2). Коды выхода 0..7 = успех, 8+ = сбой.
                "    for($try=1;$try -le 4 -and -not $ok;$try++){\r\n" +
                "      Kill-Pismo\r\n" +
                "      robocopy $src $app /E /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null\r\n" +
                "      if($LASTEXITCODE -lt 8){ $ok=$true } else { Log ('robocopy code '+$LASTEXITCODE+' (try '+$try+')'); Start-Sleep -Seconds 2 }\r\n" +
                "    }\r\n" +
                "  } else { Log 'PISMO.exe not found in archive' }\r\n" +
                "} catch { Log ('update error: '+$_.Exception.Message) }\r\n" +
                "finally {\r\n" +
                "  if($ok){ Log 'update copied OK' } else { Log 'UPDATE FAILED' }\r\n" +
                // ВСЕГДА возвращаем приложение: перезапуск в finally, ровно один экземпляр.
                "  try { if(-not (Get-Process -Name 'PISMO' -ErrorAction SilentlyContinue)){ Start-Process -FilePath (Join-Path $app 'PISMO.exe') } }\r\n" +
                "  catch { Log ('restart failed: '+$_.Exception.Message) }\r\n" +
                "  try { Remove-Item $zip -Force -ErrorAction SilentlyContinue } catch {}\r\n" +
                "  try { if($tmp){ Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue } } catch {}\r\n" +
                "  if(-not $ok){ try { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.MessageBox]::Show('Обновление не удалось (файлы заняты или папка защищена). Запущена прежняя версия. Обновите вручную со страницы релизов.','PISMO — обновление') | Out-Null } catch {} }\r\n" +
                "}\r\n";

            File.WriteAllText(ps1Path, ps, new System.Text.UTF8Encoding(false));
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} C#: launching updater (appDir={appDir}, canWrite={canWrite})\r\n"); } catch { }

            // canWrite → UseShellExecute=false: CreateProcess ПОЛНОСТЬЮ создаёт
            // дочерний powershell ДО возврата, поэтому он гарантированно стартует и
            // переживает наш Environment.Exit (проверено: так апдейтер реально
            // копировал). UseShellExecute=true ломал запуск — при мгновенном выходе
            // shell не успевал породить процесс, и powershell не запускался вовсе.
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            if (canWrite)
            {
                psi.UseShellExecute = false;
            }
            else
            {
                // Защищённая папка (Program Files) → повышение прав (UAC).
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }

            try { Process.Start(psi); }
            catch (Exception ex)
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} C#: Process.Start FAILED: {ex.Message}\r\n"); } catch { }
                // Пользователь отказал в UAC или PowerShell недоступен.
                MessageBox.Show("Не удалось запустить установку обновления:\n" + ex.Message +
                    "\n\nПопробуйте запустить PISMO от имени администратора и обновиться снова.",
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss} C#: updater launched, exiting app\r\n"); } catch { }
            Environment.Exit(0); // закрываем приложение, чтобы апдейтер смог заменить файлы
            return true;         // недостижимо, но нужно компилятору
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
