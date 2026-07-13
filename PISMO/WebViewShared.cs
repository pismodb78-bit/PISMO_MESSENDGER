using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// ЕДИНОЕ окружение WebView2 на весь процесс. КРИТИЧНО: WebView2 запрещает в
    /// одном процессе несколько CoreWebView2Environment с РАЗНЫМИ опциями — вторая
    /// такая инициализация падает с 0x8007139F (ERROR_INVALID_STATE). Раньше плееры
    /// GIF/видео создавали дефолтное окружение (EnsureCoreWebView2Async(null)), а
    /// движок звонка — своё с флагами → конфликт, и звонок не поднимался, если до
    /// него открывали медиа. Теперь ВСЕ (плееры и транспорт) берут ОДНО окружение
    /// отсюда — с флагами, нужными для звонка (они безвредны для плееров).
    /// </summary>
    internal static class WebViewShared
    {
        private static Task<CoreWebView2Environment> _envTask;
        private static readonly object _lock = new object();

        /// <summary>
        /// Корень папок данных WebView2 ВНЕ профиля пользователя. Диагностировали
        /// 0x8007139F (ERROR_INVALID_STATE) на конкретной учётке scent, при том что
        /// на свежей учётке и на другом ПК звонки поднимаются. icacls нашёл
        /// недоступный файл в %LOCALAPPDATA%\Temp этой учётки. Чтобы полностью
        /// исключить повреждённые ACL/папки в профиле, держим данные WebView2 на
        /// корне диска (C:\PISMO_wv). Если корень недоступен (нет прав/сети) —
        /// фолбэк обратно в LOCALAPPDATA.
        /// </summary>
        public static string RootDir
        {
            get
            {
                try
                {
                    string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System); // C:\Windows\System32
                    string drive = Path.GetPathRoot(string.IsNullOrEmpty(sysRoot) ? "C:\\" : sysRoot); // C:\
                    string root = Path.Combine(drive, "PISMO_wv");
                    Directory.CreateDirectory(root);
                    // Проверяем, что реально можем писать.
                    string probe = Path.Combine(root, ".w");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    return root;
                }
                catch
                {
                    string fb = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PISMO");
                    try { Directory.CreateDirectory(fb); } catch { }
                    return fb;
                }
            }
        }

        /// <summary>Общее окружение (создаётся один раз, лениво). Флаги — те же, что
        /// нужны звонку: --allow-running-insecure-content (ws:// LiveKit) + GPU/feature
        /// из настроек. Для плееров эти флаги безвредны.</summary>
        public static Task<CoreWebView2Environment> GetAsync()
        {
            lock (_lock)
            {
                if (_envTask == null || _envTask.IsFaulted || _envTask.IsCanceled)
                    _envTask = CreateAsync();
                return _envTask;
            }
        }

        private static async Task<CoreWebView2Environment> CreateAsync()
        {
            string udf = Path.Combine(RootDir, "webview-shared");
            const string baseArgs =
                "--allow-running-insecure-content --autoplay-policy=no-user-gesture-required";

            // Пробуем с полными флагами; если видеодрайвер их валит (0x8007139F) —
            // фолбэк на программный рендер, затем на минимум. Свежая папка на
            // фолбэках, чтобы залипший процесс не мешал.
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string args = attempt == 0 ? DeviceSettings.WebViewArgs(baseArgs)
                                : attempt == 1 ? baseArgs + " --disable-gpu --disable-gpu-compositing"
                                : baseArgs;
                    string folder = attempt == 0 ? udf : udf + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var opts = new CoreWebView2EnvironmentOptions(args);
                    return await CoreWebView2Environment.CreateAsync(null, folder, opts);
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Diagnostics.Debug.WriteLine($"[WebViewShared retry {attempt}] {ex.Message}");
                    try { await Task.Delay(300 * (attempt + 1)); } catch { }
                }
            }
            throw last ?? new Exception("WebViewShared init failed");
        }
    }
}
